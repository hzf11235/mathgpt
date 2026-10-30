using MathGPT.Core;

namespace MathGPT.App;

public sealed class AppServices
{
    public StateStore Store { get; }
    public WebsiteClient Site { get; }
    public CatalogService Catalog { get; }
    public PackageService Packages { get; }
    public BitsDownload Downloads { get; }
    public ClientService Clients { get; }
    public TaskQueue Queue { get; }
    public AppServices(string root)
    {
        Store = new(root); Site = new(Store); Catalog = new(Store, Site);
        Packages = new(Store); Downloads = new(Store); Clients = new(Store);
        Queue = new(Store, new InferenceService(Store), Catalog);
    }
}
