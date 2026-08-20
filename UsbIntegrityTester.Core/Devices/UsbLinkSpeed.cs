namespace UsbIntegrityTester.Core.Devices;

/// <summary>Negotiated USB link speed, as reported by the hub the device is plugged into — not the nominal port label.</summary>
public enum UsbLinkSpeed
{
    Unknown = 0,
    Low1_5Mbps,
    Full12Mbps,
    High480Mbps,
    Super5Gbps,
    SuperPlus10Gbps,
    SuperPlus20Gbps,
}

public static class UsbLinkSpeedExtensions
{
    /// <summary>Theoretical max throughput in MB/s for the negotiated link, used as the "spec ceiling" for the report.</summary>
    public static double MaxTheoreticalMegabytesPerSecond(this UsbLinkSpeed speed) => speed switch
    {
        UsbLinkSpeed.Low1_5Mbps => 0.02,
        UsbLinkSpeed.Full12Mbps => 1.5,
        UsbLinkSpeed.High480Mbps => 60,
        UsbLinkSpeed.Super5Gbps => 500,
        UsbLinkSpeed.SuperPlus10Gbps => 1000,
        UsbLinkSpeed.SuperPlus20Gbps => 2000,
        _ => 0,
    };

    public static string DisplayName(this UsbLinkSpeed speed) => speed switch
    {
        UsbLinkSpeed.Low1_5Mbps => "USB 1.0 Low Speed (1.5 Mbps)",
        UsbLinkSpeed.Full12Mbps => "USB 1.1 Full Speed (12 Mbps)",
        UsbLinkSpeed.High480Mbps => "USB 2.0 High Speed (480 Mbps)",
        UsbLinkSpeed.Super5Gbps => "USB 3.2 Gen 1 (5 Gbps)",
        // Windows' IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 can confirm "SuperSpeedPlus or
        // higher" but doesn't expose negotiated lane count, so Gen 2 (10 Gbps) and Gen 2x2
        // (20 Gbps) can't be told apart without reading the device's BOS descriptor — out of
        // scope for now. SuperPlus10Gbps is the value auto-detection reports for either case.
        UsbLinkSpeed.SuperPlus10Gbps => "USB 3.2 Gen 2 or faster (10+ Gbps)",
        UsbLinkSpeed.SuperPlus20Gbps => "USB 3.2 Gen 2x2 (20 Gbps)",
        _ => "Unknown",
    };
}
