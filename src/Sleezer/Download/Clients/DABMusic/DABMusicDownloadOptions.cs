using NzbDrone.Plugin.Sleezer.Download.Base;

namespace NzbDrone.Plugin.Sleezer.Download.Clients.DABMusic
{
    public record DABMusicDownloadOptions : BaseDownloadOptions
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;

        public DABMusicDownloadOptions() : base() { }

        protected DABMusicDownloadOptions(DABMusicDownloadOptions options) : base(options)
        {
            Email = options.Email;
            Password = options.Password;
        }
    }
}