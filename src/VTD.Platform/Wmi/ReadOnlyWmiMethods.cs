// Audited exception (banned/rs0030-allowlist.txt): WMI METHOD calls are banned in the read side because a method can
// change state. This file is the only place that invokes one, and only the read-only methods in AllowedMethods:
// BcdStore.OpenStore / BcdStore.OpenObject / BcdObject.GetElement (reads of the boot configuration store) and
// Win32_Tpm.IsReady. No Set*, Delete*, Create*, Import*, Export* or any other method can be reached from here.
#pragma warning disable RS0030

using System.Globalization;
using System.Management;
using VTD.Platform.Sources;

namespace VTD.Platform.Wmi;

/// <summary>The allowlisted read-only WMI method calls (admin).</summary>
public sealed class ReadOnlyWmiMethods : IWmiMethodSource
{
    /// <summary>The only WMI methods VTDiag's read side may call.</summary>
    public static IReadOnlyList<string> AllowedMethods { get; } = ["OpenStore", "OpenObject", "GetElement", "IsReady"];

    private const string CurrentBootEntry = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";
    private const string FirmwareBootManager = "{a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba}";
    private const uint TestSigning = 0x16000049;
    private const uint NoIntegrityChecks = 0x16000048;
    private const uint NxPolicy = 0x25000020;
    private const uint HypervisorLaunchType = 0x250000F0;
    private const uint SafeBoot = 0x25000080;
    private const uint KernelDebugger = 0x260000A0;
    private const uint DisplayOrder = 0x24000001;
    private const uint ApplicationPath = 0x12000002;

    /// <inheritdoc />
    public BcdRaw ReadBcd(TimeSpan timeout)
    {
        var task = Task.Run(ReadBcdCore);
        try
        {
            return task.Wait(timeout) ? task.Result : new BcdRaw { Status = ReadStatus.TimedOut };
        }
        catch (AggregateException e)
        {
            return new BcdRaw { Status = WmiSource.Map(e.InnerException ?? e).Status };
        }
    }

    /// <inheritdoc />
    public TpmWmiRaw ReadTpm(TimeSpan timeout)
    {
        var task = Task.Run(ReadTpmCore);
        try
        {
            return task.Wait(timeout) ? task.Result : new TpmWmiRaw { Status = ReadStatus.TimedOut };
        }
        catch (AggregateException e)
        {
            return new TpmWmiRaw { Status = WmiSource.Map(e.InnerException ?? e).Status };
        }
    }

    private static BcdRaw ReadBcdCore()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            scope.Connect();
            using var storeClass = new ManagementClass(scope, new ManagementPath("BcdStore"), null);
            using var openIn = storeClass.GetMethodParameters("OpenStore");
            openIn["File"] = string.Empty;
            using var openOut = Invoke(storeClass, "OpenStore", openIn);
            if (openOut is null || !IsTrue(openOut["ReturnValue"]))
            {
                return new BcdRaw { Status = ReadStatus.AccessDenied };
            }

            using var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=''"), null);
            using var current = OpenObject(scope, store, CurrentBootEntry);
            if (current is null)
            {
                return new BcdRaw { Status = ReadStatus.NotFound };
            }

            var raw = new BcdRaw
            {
                Status = ReadStatus.Ok,
                TestSigning = GetBool(current, TestSigning) ?? false,
                NoIntegrityChecks = GetBool(current, NoIntegrityChecks) ?? false,
                Nx = GetInteger(current, NxPolicy),
                HypervisorLaunchType = GetInteger(current, HypervisorLaunchType),
                SafeBoot = GetInteger(current, SafeBoot),
                Debug = GetBool(current, KernelDebugger) ?? false,
            };

            using var fw = OpenObject(scope, store, FirmwareBootManager);
            if (fw is null)
            {
                return raw;
            }

            var ids = GetIds(fw, DisplayOrder);
            var paths = new List<string>();
            foreach (var id in ids.Take(32))
            {
                using var entry = OpenObject(scope, store, id);
                var path = entry is null ? null : GetString(entry, ApplicationPath);
                paths.Add(path?.ToLowerInvariant() ?? string.Empty);
            }

            return raw with { FirmwareEntryPaths = paths, FirmwareEntriesRead = true };
        }
        catch (Exception e)
        {
            return new BcdRaw { Status = WmiSource.Map(e).Status };
        }
    }

    private static TpmWmiRaw ReadTpmCore()
    {
        try
        {
            var scope = new ManagementScope(@"root\cimv2\Security\MicrosoftTpm");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_Tpm"));
            using var results = searcher.Get();
            foreach (ManagementObject tpm in results)
            {
                using (tpm)
                {
                    bool? ready = null;
                    using var readyOut = Invoke(tpm, "IsReady", null);
                    if (readyOut is not null && IsZero(readyOut["ReturnValue"]))
                    {
                        ready = readyOut["IsReady"] is bool b ? b : null;
                    }

                    var spec = tpm["SpecVersion"] as string;
                    return new TpmWmiRaw
                    {
                        Status = ReadStatus.Ok,
                        SpecVersion = spec?.Split(',')[0].Trim(),
                        ManufacturerIdTxt = (tpm["ManufacturerIdTxt"] as string)?.Trim(),
                        Enabled = tpm["IsEnabled_InitialValue"] as bool?,
                        Activated = tpm["IsActivated_InitialValue"] as bool?,
                        Ready = ready,
                    };
                }
            }

            return new TpmWmiRaw { Status = ReadStatus.NotFound };
        }
        catch (Exception e)
        {
            return new TpmWmiRaw { Status = WmiSource.Map(e).Status };
        }
    }

    private static ManagementBaseObject? Invoke(ManagementObject target, string method, ManagementBaseObject? input)
    {
        if (!AllowedMethods.Contains(method, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("WMI method is not on the read-only allowlist: " + method);
        }

        return target.InvokeMethod(method, input, null);
    }

    private static ManagementObject? OpenObject(ManagementScope scope, ManagementObject store, string id)
    {
        using var input = store.GetMethodParameters("OpenObject");
        input["Id"] = id;
        using var output = Invoke(store, "OpenObject", input);
        if (output is null || !IsTrue(output["ReturnValue"]))
        {
            return null;
        }

        var path = string.Create(CultureInfo.InvariantCulture, $"BcdObject.Id=\"{id}\",StoreFilePath=\"\"");
        return new ManagementObject(scope, new ManagementPath(path), null);
    }

    private static ManagementBaseObject? GetElement(ManagementObject obj, uint type)
    {
        using var input = obj.GetMethodParameters("GetElement");
        input["Type"] = type;
        var output = Invoke(obj, "GetElement", input);
        return output is not null && IsTrue(output["ReturnValue"]) ? output["Element"] as ManagementBaseObject : null;
    }

    private static bool? GetBool(ManagementObject obj, uint type)
    {
        using var e = GetElement(obj, type);
        return e?["Boolean"] as bool?;
    }

    private static long? GetInteger(ManagementObject obj, uint type)
    {
        using var e = GetElement(obj, type);
        return e?["Integer"] switch
        {
            ulong u => unchecked((long)u),
            long l => l,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
            _ => null,
        };
    }

    private static string? GetString(ManagementObject obj, uint type)
    {
        using var e = GetElement(obj, type);
        return e?["String"] as string;
    }

    private static IReadOnlyList<string> GetIds(ManagementObject obj, uint type)
    {
        using var e = GetElement(obj, type);
        return e?["Ids"] as string[] ?? [];
    }

    private static bool IsTrue(object? value) => value is bool b && b;

    private static bool IsZero(object? value) => value is uint u ? u == 0 : value is int i && i == 0;
}
