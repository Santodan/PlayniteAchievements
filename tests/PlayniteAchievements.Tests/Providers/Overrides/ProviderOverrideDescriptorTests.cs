using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Overrides;

namespace PlayniteAchievements.Tests.Providers.Overrides
{
    [TestClass]
    public class ProviderOverrideDescriptorTests
    {
        [TestMethod]
        public void None_AlwaysValid_WithNullValue()
        {
            var descriptor = ProviderOverrideDescriptor.None();

            Assert.AreEqual(ProviderOverrideValueKind.None, descriptor.ValueKind);
            Assert.IsTrue(descriptor.ValueOptional);

            var result = descriptor.Validate("ignored");
            Assert.IsTrue(result.IsValid);
            Assert.IsNull(result.NormalizedValue);
        }

        [TestMethod]
        public void RequiredText_RejectsEmpty_AndTrimsValue()
        {
            var descriptor = ProviderOverrideDescriptor.Text(
                "label.key",
                ProviderOverrideValidators.RequiredText);

            Assert.AreEqual(ProviderOverrideValueKind.Text, descriptor.ValueKind);

            var empty = descriptor.Validate("   ");
            Assert.IsFalse(empty.IsValid);
            Assert.AreEqual(ProviderOverrideValidators.RequiredValueErrorKey, empty.ErrorMessageKey);

            var valid = descriptor.Validate("  OFR.123  ");
            Assert.IsTrue(valid.IsValid);
            Assert.AreEqual("OFR.123", valid.NormalizedValue);
        }

        [TestMethod]
        public void Choice_ValidatesMembership_CaseInsensitive_AndNormalizesToCanonicalValue()
        {
            var descriptor = ProviderOverrideDescriptor.Choice(
                "label.key",
                new[]
                {
                    new ProviderOverrideChoice("Wow", "World of Warcraft"),
                    new ProviderOverrideChoice("Sc2", "StarCraft II")
                },
                "invalid.key");

            Assert.AreEqual(ProviderOverrideValueKind.Choice, descriptor.ValueKind);

            var valid = descriptor.Validate("wow");
            Assert.IsTrue(valid.IsValid);
            Assert.AreEqual("Wow", valid.NormalizedValue, "Choice should normalize to the declared casing.");

            var invalid = descriptor.Validate("Diablo");
            Assert.IsFalse(invalid.IsValid);
            Assert.AreEqual("invalid.key", invalid.ErrorMessageKey);
        }

        [TestMethod]
        public void Choice_GetValueDisplay_ReturnsChoiceLabel()
        {
            var descriptor = ProviderOverrideDescriptor.Choice(
                "label.key",
                new[] { new ProviderOverrideChoice("Sc2", "StarCraft II") },
                "invalid.key");

            Assert.AreEqual("StarCraft II", descriptor.GetValueDisplay("Sc2"));
            // Unknown values fall back to the raw value.
            Assert.AreEqual("unknown", descriptor.GetValueDisplay("unknown"));
        }
    }
}
