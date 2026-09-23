using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Plugin.Sleezer.Metadata.ScheduledTasks
{
    // Async so it runs after every synchronous ApplicationStartedEvent handler — including
    // TaskManager's purge of ScheduledTasks rows it does not own.
    public class ScheduledTaskServiceStarter(IScheduledTaskService _taskService) : IHandleAsync<ApplicationStartedEvent>
    {
        public void HandleAsync(ApplicationStartedEvent message) => _taskService.InitializeTasks();
    }
}