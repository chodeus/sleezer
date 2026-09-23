using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks
{
    public interface IScheduledTaskService
    {
        void InitializeTasks();
    }

    public class ScheduledTaskService(
        IScheduledTaskRepository _scheduledTaskRepository,
        IMetadataFactory _metadataFactory,
        ICacheManager _cacheManager,
        Logger _logger) : IScheduledTaskService, IHandle<ProviderUpdatedEvent<IMetadata>>, IHandle<ProviderAddedEvent<IMetadata>>, IHandle<ProviderDeletedEvent<IMetadata>>
    {
        // Keyed by command type; a later registration replaces the prior provider for that type.
        private readonly Dictionary<string, IProvideScheduledTask> _registeredTasks = [];

        // InitializeTasks runs on an async handler while provider events arrive on other threads,
        // and every _registeredTasks access below is a check-then-act.
        private readonly object _gate = new();

        public void InitializeTasks()
        {
            _logger.Trace("Initializing scheduled task system");

            int registered;
            int available;

            // Collected inside the lock: a delete arriving between the read and the loop would
            // find nothing registered to disable, and startup would then register it anyway.
            lock (_gate)
            {
                IProvideScheduledTask[] taskProviders = _metadataFactory.GetAvailableProviders()
                    .OfType<IProvideScheduledTask>()
                    .Where(ValidateTaskProvider)
                    .DistinctBy(x => x.CommandType.FullName)
                    .ToArray();

                foreach (IProvideScheduledTask provider in taskProviders.Where(x => (x as IProvider)?.Definition?.Enable == true))
                    EnableTask(provider);

                registered = _registeredTasks.Count;
                available = taskProviders.Length;
            }

            _logger.Debug($"Initialized scheduled task system: {registered} active tasks, {available} total task providers");
        }

        public void Handle(ProviderUpdatedEvent<IMetadata> message) => Apply(message.Definition);

        public void Handle(ProviderAddedEvent<IMetadata> message) => Apply(message.Definition);

        public void Handle(ProviderDeletedEvent<IMetadata> message)
        {
            lock (_gate)
            {
                if (_registeredTasks.Values.FirstOrDefault(x => (x as IProvider)?.Definition?.Id == message.ProviderId) is { } taskProvider)
                    DisableTask(taskProvider);
            }
        }

        // Resolved from the saved definition, so registration turns on Enable and the interval.
        private void Apply(ProviderDefinition definition)
        {
            lock (_gate)
            {
                if (ResolveTaskProvider(definition) is not { } taskProvider)
                {
                    // A provider that stopped resolving must not keep firing the task it registered.
                    if (_registeredTasks.Values.FirstOrDefault(x => (x as IProvider)?.Definition?.Id == definition.Id) is { } stale)
                        DisableTask(stale);

                    return;
                }

                _logger.Trace($"Provider event for: {(taskProvider as IProvider)?.Name}, Enabled: {definition.Enable}");

                if (definition.Enable)
                    EnableTask(taskProvider);
                else
                    DisableTask(taskProvider);
            }
        }

        private IProvideScheduledTask? ResolveTaskProvider(ProviderDefinition definition)
        {
            if (definition is not MetadataDefinition metadataDefinition)
                return null;

            if (metadataDefinition.Implementation == null)
            {
                _logger.Warn($"Metadata definition {metadataDefinition.Id} ({metadataDefinition.Name}) has no implementation; its scheduled task is not registered");
                return null;
            }

            try
            {
                // Startup resolves through ProviderFactory.Active(), which drops definitions whose
                // settings do not validate; an event must not register what startup would skip.
                if (!metadataDefinition.Settings.Validate().IsValid)
                {
                    _logger.Warn($"Settings for {metadataDefinition.Implementation} are not valid; its scheduled task is not registered");
                    return null;
                }

                // Ordinary metadata providers resolve fine and are simply not task providers.
                return _metadataFactory.GetInstance(metadataDefinition) is IProvideScheduledTask provider && ValidateTaskProvider(provider)
                    ? provider
                    : null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Cannot resolve a scheduled task provider for {metadataDefinition.Implementation}; its task is not registered");
                return null;
            }
        }

        private void EnableTask(IProvideScheduledTask provider)
        {
            if (provider.IntervalMinutes <= 0)
            {
                _logger.Trace($"Task has interval <= 0, treating as disabled: {(provider as IProvider)?.Name}");
                DisableTask(provider);
                return;
            }

            string typeName = provider.CommandType.FullName!;

            if (_registeredTasks.ContainsKey(typeName))
            {
                // The fresh instance carries the saved definition, so it replaces the stored one.
                _registeredTasks[typeName] = provider;
                _logger.Trace($"Task already enabled: {(provider as IProvider)?.Name}");
                UpdateTaskInterval(provider);
                return;
            }

            if (RegisterTask(provider))
                _logger.Info($"Enabled scheduled task: {(provider as IProvider)?.Name} (Interval: {provider.IntervalMinutes}m, Priority: {provider.Priority})");
        }

        private void DisableTask(IProvideScheduledTask provider)
        {
            string typeName = provider.CommandType.FullName!;

            if (!_registeredTasks.TryGetValue(typeName, out IProvideScheduledTask? registered))
            {
                _logger.Debug($"Task already disabled: {(provider as IProvider)?.Name}");
                return;
            }

            // Two definitions sharing a command type must not delete each other's task.
            int? registeredId = (registered as IProvider)?.Definition?.Id;
            int? callerId = (provider as IProvider)?.Definition?.Id;

            if (registeredId != null && callerId != null && registeredId != callerId)
            {
                _logger.Debug($"Definition {callerId} does not own the task for {typeName}; leaving it registered to {registeredId}");
                return;
            }

            string? name = (provider as IProvider)?.Name;

            // Each step runs even if another throws. The cache is what TaskManager fires from, so it
            // goes first; a row left behind is purged by TaskManager at the next startup.
            bool uncached = Attempt(() => RemoveFromCache(typeName), "remove the cached scheduled task", name);
            bool deleted = Attempt(() =>
            {
                if (_scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName) is { } existing)
                    _scheduledTaskRepository.Delete(existing.Id);
            }, "delete the scheduled task from the repository", name);
            _registeredTasks.Remove(typeName);

            if (uncached && deleted)
                _logger.Info($"Disabled scheduled task: {name}");
            else
                _logger.Warn($"Scheduled task only partly disabled: {name} (uncached: {uncached}, row deleted: {deleted})");
        }

        private bool Attempt(Action step, string what, string? name)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to {what}: {name}");
                return false;
            }
        }

        private bool RegisterTask(IProvideScheduledTask provider)
        {
            string typeName = provider.CommandType.FullName!;

            try
            {
                ScheduledTask task = new()
                {
                    Interval = provider.IntervalMinutes,
                    TypeName = typeName,
                    Priority = provider.Priority
                };

                ScheduledTask? existing = _scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName);

                if (existing != null)
                {
                    existing.Interval = task.Interval;
                    existing.Priority = task.Priority;
                    _scheduledTaskRepository.Update(existing);
                    _logger.Trace($"Updated existing scheduled task: {(provider as IProvider)?.Name}");
                }
                else
                {
                    task.LastExecution = DateTime.UtcNow;
                    _scheduledTaskRepository.Insert(task);
                    _logger.Trace($"Inserted new scheduled task: {(provider as IProvider)?.Name}");
                }

                UpdateCache(existing ?? task);
                _registeredTasks[typeName] = provider;
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to register scheduled task: {(provider as IProvider)?.Name}");
                return false;
            }
        }

        private void UpdateTaskInterval(IProvideScheduledTask provider)
        {
            string typeName = provider.CommandType.FullName!;
            try
            {
                ScheduledTask? existing = _scheduledTaskRepository.All().SingleOrDefault(t => t.TypeName == typeName);

                if (existing != null && existing.Interval != provider.IntervalMinutes)
                {
                    existing.Interval = provider.IntervalMinutes;
                    _scheduledTaskRepository.Update(existing);
                    UpdateCache(existing);
                    _logger.Trace($"Updated task interval for {(provider as IProvider)?.Name}: {provider.IntervalMinutes}m");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to update task interval for: {(provider as IProvider)?.Name}");
            }
        }

        private void UpdateCache(ScheduledTask task)
        {
            ICached<ScheduledTask> cache = _cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
            cache.Set(task.TypeName, task);
        }

        private void RemoveFromCache(string typeName)
        {
            ICached<ScheduledTask> cache = _cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
            cache.Remove(typeName);
        }

        private bool ValidateTaskProvider(IProvideScheduledTask provider)
        {
            try
            {
                if (provider.CommandType == null)
                {
                    _logger.Warn($"Task provider {(provider as IProvider)?.Name} has no command type. Provider will be excluded.");
                    return false;
                }

                if (provider.IntervalMinutes < 0)
                {
                    _logger.Warn($"Task provider {(provider as IProvider)?.Name} has invalid interval ({provider.IntervalMinutes}m). Provider will be excluded.");
                    return false;
                }

                _logger.Trace($"Validated task provider: {(provider as IProvider)?.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Failed to validate task provider {(provider as IProvider)?.Name}. Provider will be excluded.");
                return false;
            }
        }
    }
}