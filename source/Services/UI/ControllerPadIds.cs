using System.Collections.Generic;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Controller families this plugin recognises by USB vendor/product id.
    /// </summary>
    internal enum PadFamily
    {
        DualSense,
        DualShock4,
        SteamController,
    }

    /// <summary>
    /// The vendor/product identity table shared by the features that have to recognise a pad:
    /// <see cref="ControllerHidRumble"/> writes its rumble report, and the recorder's haptic
    /// endpoint classifier decides whether a render endpoint belongs to one.
    ///
    /// Ids are transcribed from SDL's hidapi drivers (SDL_hidapi_ps5.c, SDL_hidapi_ps4.c,
    /// SDL_hidapi_steam_triton.c, controller_list.h).
    /// </summary>
    internal static class ControllerPadIds
    {
        /// <summary>Sony's USB vendor id.</summary>
        public const int SonyVendorId = 0x054c;

        /// <summary>Valve's USB vendor id.</summary>
        public const int ValveVendorId = 0x28de;

        public static readonly IReadOnlyDictionary<int, PadFamily> KnownPads = new Dictionary<int, PadFamily>
        {
            // Sony, vendor 0x054c.
            { Key(SonyVendorId, 0x0ce6), PadFamily.DualSense },   // DualSense
            { Key(SonyVendorId, 0x0df2), PadFamily.DualSense },   // DualSense Edge
            { Key(SonyVendorId, 0x05c4), PadFamily.DualShock4 },  // DualShock 4
            { Key(SonyVendorId, 0x09cc), PadFamily.DualShock4 },  // DualShock 4 slim
            { Key(SonyVendorId, 0x0ba0), PadFamily.DualShock4 },  // DualShock 4 wireless dongle
            { Key(SonyVendorId, 0x05c5), PadFamily.DualShock4 },  // DualShock 4 strikepad

            // Valve, vendor 0x28de. 0x1302/0x1303 are the 2026 controller itself (wired and BLE);
            // 0x1304/0x1305 are its Proteus and Nereid dongles.
            { Key(ValveVendorId, 0x1302), PadFamily.SteamController },
            { Key(ValveVendorId, 0x1303), PadFamily.SteamController },
            { Key(ValveVendorId, 0x1304), PadFamily.SteamController },
            { Key(ValveVendorId, 0x1305), PadFamily.SteamController },
        };

        /// <summary>Packs a vendor/product pair into the dictionary key.</summary>
        public static int Key(int vendorId, int productId)
        {
            return ((vendorId & 0xffff) << 16) | (productId & 0xffff);
        }

        /// <summary>
        /// Whether this pair is a pad whose haptics are driven by an audio stream: the Sony pads
        /// expose a render endpoint over USB and play their haptic waveform through it. The Steam
        /// Controller drives its actuators over HID only, so its ids do not qualify.
        /// </summary>
        public static bool RendersHapticsAsAudio(int vendorId, int productId)
        {
            return KnownPads.TryGetValue(Key(vendorId, productId), out var family) &&
                   (family == PadFamily.DualSense || family == PadFamily.DualShock4);
        }
    }
}
