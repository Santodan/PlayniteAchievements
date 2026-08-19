using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class HapticEndpointClassifierTests
    {
        [TestMethod]
        public void UsbDualSenseEndpointIsHaptic()
        {
            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                @"USB\VID_054C&PID_0CE6&MI_03\7&1e2fdd2a&0&0003",
                "Speakers (Wireless Controller)",
                "Wireless Controller"));
        }

        [TestMethod]
        public void DualShock4EndpointIsHaptic()
        {
            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                @"USB\VID_054C&PID_09CC&MI_03\6&2c1f7b91&0&0003",
                "Speakers",
                "Wireless Controller"));
        }

        [TestMethod]
        public void BluetoothVendorFormIsHaptic()
        {
            // The Bluetooth enumerator prefixes the vendor id with its id-space, so only the last
            // four hex digits are the vendor itself.
            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                @"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0002054C_PID&0CE6\7&852df7&0",
                null,
                null));
        }

        [TestMethod]
        public void SteamControllerIsNotHaptic()
        {
            // Valve's pad drives its actuators over HID, so an audio endpoint of its own is not a
            // haptic feed and must not be subtracted from clip audio.
            Assert.IsFalse(HapticEndpointClassifier.IsHapticEndpoint(
                @"USB\VID_28DE&PID_1302&MI_01\8&1b4f0e6a&0&0001",
                "Speakers (Steam Controller)",
                null));
        }

        [TestMethod]
        public void OtherSonyAudioDeviceIsNotHaptic()
        {
            // Same vendor, a product that is not a pad: the id table decides, not the vendor.
            Assert.IsFalse(HapticEndpointClassifier.IsHapticEndpoint(
                @"USB\VID_054C&PID_0DE0&MI_00\9&2a3b4c5d&0&0000",
                "Speakers (Sony Headset)",
                null));
        }

        [TestMethod]
        public void NameIdentifiesAnEndpointWithNoVendorPairInItsId()
        {
            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                @"SWD\MMDEVAPI\{0.0.0.00000000}.{2b8f4c19-6d1e-4f77-9a2c-5e0d4a6b1c33}",
                "Speakers (DualSense Wireless Controller)",
                null));
        }

        [TestMethod]
        public void OrdinaryOutputDeviceIsNotHaptic()
        {
            Assert.IsFalse(HapticEndpointClassifier.IsHapticEndpoint(
                @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\4&2f1a3b4c&0&0001",
                "Speakers (Realtek High Definition Audio)",
                "Realtek High Definition Audio"));
        }

        [TestMethod]
        public void MissingIdentityIsNotHaptic()
        {
            Assert.IsFalse(HapticEndpointClassifier.IsHapticEndpoint((string)null, null, null));
        }

        [TestMethod]
        public void AnyCandidateCarryingThePadIdentifiesTheEndpoint()
        {
            // Real endpoints publish the hardware id under whichever property the driver chose, so
            // the whole set is offered and one match is enough.
            var candidates = new[]
            {
                @"SWD\MMDEVAPI\{0.0.0.00000000}.{2b8f4c19-6d1e-4f77-9a2c-5e0d4a6b1c33}",
                @"{1}.USB\VID_054C&PID_0CE6&MI_03\6&1bcb3ef7&0&0000",
                @"\\?\usb#vid_054c&pid_0ce6&mi_03#6&1bcb3ef7&0&0000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}",
            };

            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(candidates, "Speakers", null));
        }

        [TestMethod]
        public void AnUnrelatedVendorIdDoesNotVetoTheName()
        {
            // An endpoint can publish the vendor id of a hub, a dongle or a composite parent rather
            // than the pad's own, so an unrecognised id must not override a name that says it is a
            // controller: a pad the scan misses makes the whole feature dead.
            var candidates = new[] { @"{1}.USB\VID_8087&PID_0026\6&1bcb3ef7&0&0000" };

            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                candidates, "Speakers (Wireless Controller)", null));
        }

        [TestMethod]
        public void NoPublishedIdentityFallsBackToTheName()
        {
            Assert.IsTrue(HapticEndpointClassifier.IsHapticEndpoint(
                new string[0], "Speakers (Wireless Controller)", null));
        }

        [TestMethod]
        public void ShortVendorFieldIsNotParsed()
        {
            // A truncated id must not be read as some other pad by accident.
            Assert.IsFalse(
                HapticEndpointClassifier.TryParseVendorProduct(@"USB\VID_54C&PID_0CE6\x", out _, out _));
        }
    }
}
