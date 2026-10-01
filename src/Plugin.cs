using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MangaReader;

public sealed class Configuration : BasePluginConfiguration
{
    public Guid MangaLibraryId { get; set; }
    public Guid[] MangaLibraryIds { get; set; } = [];
    public bool EnableInAppReader { get; set; } = true;
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) { Instance = this; }
    public override string Name => "Manga Reader";
    public override string Description => "Read CBZ manga with page turning and personal, server-saved progress.";
    public override Guid Id => Guid.Parse("42cc80cc-2832-4b92-a583-06ab560eca34");
    public IEnumerable<PluginPageInfo> GetPages() => [new()
    {
        Name = "manga-reader", DisplayName = "Manga Reader",
        EmbeddedResourcePath = "Jellyfin.Plugin.MangaReader.Web.config.html"
    }, new() { Name = "manga-reader-config.js", EmbeddedResourcePath = "Jellyfin.Plugin.MangaReader.Web.config.js" }];
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<ProgressStore>();
        services.AddSingleton<ArchiveReader>();
        services.AddTransient<IStartupFilter, ReaderStartupFilter>();
    }
}
