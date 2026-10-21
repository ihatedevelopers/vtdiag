using VTD.Contracts;
using VTD.Contracts.Snapshots;

namespace VTD.Reporting;

/// <summary>
/// Live entities of each <see cref="EntityKind"/> in a fresh (local-level) snapshot: the candidates a helper plan's
/// share-level ids are matched against. Only Known facts contribute; pack collectors' entities are included.
/// </summary>
internal static class PlanEntityCandidates
{
    private static readonly string[] ProtectedServices = ["vgc", "vgk"];

    /// <summary>
    /// A target no helper plan may bind (defence in depth; the engine's ProtectedTargetPolicy stays authoritative):
    /// Riot Vanguard's services, driver and files, Riot/Vanguard/League processes, the adapter that carries the default
    /// route, and the boot disk's storage controller.
    /// </summary>
    /// <param name="s">Fresh snapshot.</param>
    /// <param name="e">Resolved live entity.</param>
    public static bool IsProtected(ScanSnapshot s, EntityRef e) => e.Kind switch
    {
        EntityKind.Service => ProtectedServices.Contains(e.Id, StringComparer.OrdinalIgnoreCase),
        EntityKind.Process => RiotProcessNames.IsProtected(e.Id),
        EntityKind.Driver => string.Equals(e.Id, "vgk.sys", StringComparison.OrdinalIgnoreCase),
        EntityKind.File or EntityKind.CompatLayer => e.Id.Contains("\\Riot Vanguard\\", StringComparison.OrdinalIgnoreCase),
        EntityKind.Adapter or EntityKind.Device => IsDefaultRouteAdapter(s, e.Id) || IsBootController(s, e.Id),
        _ => false,
    };

    private static bool IsDefaultRouteAdapter(ScanSnapshot s, string id)
        => s.Network.Adapters.TryGetValue(out var adapters) && adapters.Any(a => a.IsDefaultRoute && string.Equals(a.InstanceId, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsBootController(ScanSnapshot s, string id)
        => s.Storage.Controllers.TryGetValue(out var controllers) && controllers.Any(c => c.IsBootController && string.Equals(c.InstanceId, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<EntityRef> Of(ScanSnapshot s, EntityKind kind)
    {
        var list = new List<EntityRef>();
        void Add(string? id, string? display)
        {
            if (!string.IsNullOrEmpty(id) && !list.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new EntityRef { Kind = kind, Id = id, DisplayName = display });
            }
        }

        switch (kind)
        {
            case EntityKind.Adapter:
                if (s.Network.Adapters.TryGetValue(out var adapters))
                {
                    adapters.ToList().ForEach(a => Add(a.InstanceId, a.Description));
                }

                break;
            case EntityKind.Device:
                if (s.Devices.TryGetValue(out var devices))
                {
                    devices.ToList().ForEach(d => Add(d.InstanceId, d.FriendlyName));
                }

                break;
            case EntityKind.Service:
                if (s.Services.TryGetValue(out var services))
                {
                    services.ToList().ForEach(x => Add(x.Name, x.DisplayName));
                }

                if (s.Riot.Vanguard.TryGetValue(out var vanguard))
                {
                    Add(vanguard.Vgc?.Name, vanguard.Vgc?.DisplayName);
                    Add(vanguard.Vgk?.Name, vanguard.Vgk?.DisplayName);
                }

                break;
            case EntityKind.Process:
                if (s.Processes.TryGetValue(out var processes))
                {
                    processes.ToList().ForEach(p => Add(p.Name, p.Name));
                }

                break;
            case EntityKind.Driver:
                if (s.Drivers.TryGetValue(out var drivers))
                {
                    drivers.ToList().ForEach(d => Add(d.Name, d.Name));
                }

                break;
            case EntityKind.App:
                if (s.Apps.TryGetValue(out var apps))
                {
                    apps.ToList().ForEach(a => Add(EntityIds.App(a.Hive, a.KeyName), a.DisplayName));
                }

                break;
            case EntityKind.StartupItem:
                if (s.Startup.TryGetValue(out var startup))
                {
                    startup.ToList().ForEach(x => Add(EntityIds.StartupItem(x.Location, x.Name), x.Name));
                }

                break;
            case EntityKind.CompatLayer:
                if (s.Riot.CompatLayers.TryGetValue(out var layers))
                {
                    layers.ToList().ForEach(l => Add(l.ExePath, l.ImageName));
                }

                break;
            case EntityKind.File:
                if (s.Drivers.TryGetValue(out var driverFiles))
                {
                    driverFiles.ToList().ForEach(d => Add(d.Path, d.Name));
                }

                if (s.Services.TryGetValue(out var serviceFiles))
                {
                    serviceFiles.ToList().ForEach(x => Add(x.ImagePath, x.Name));
                }

                if (s.Startup.TryGetValue(out var startupFiles))
                {
                    startupFiles.ToList().ForEach(x => Add(x.TargetPath, x.ImageName));
                }

                break;
            case EntityKind.AudioEndpoint:
                if (s.Audio.Endpoints.TryGetValue(out var endpoints))
                {
                    endpoints.ToList().ForEach(e => Add(e.Id, e.Name));
                }

                break;
            case EntityKind.WindowsFeature:
                if (s.OptionalFeatures.TryGetValue(out var features))
                {
                    features.ToList().ForEach(f => Add(f, f));
                }

                break;
            default:
                break;
        }

        foreach (var pack in s.Pack)
        {
            if (pack.Data.TryGetValue(out var data))
            {
                foreach (var e in data.Entities.Where(e => e.Kind == kind))
                {
                    Add(e.Id, e.DisplayName);
                }
            }
        }

        return list;
    }
}
