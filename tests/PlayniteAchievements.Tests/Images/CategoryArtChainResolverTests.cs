using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Tests.Images
{
    // Covers the override side of the category art chain and its ancestor inheritance. Provider
    // default art is deliberately out of scope here: with no DiskImageServiceAccessor installed,
    // CategoryDefaultImageResolver returns null, which keeps these cases hermetic.
    [TestClass]
    public class CategoryArtChainResolverTests
    {
        private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private static Dictionary<string, CategoryImageOverrideData> Overrides(
            params (string Label, string Art)[] entries)
        {
            var result = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                result[entry.Label] = new CategoryImageOverrideData { Art = entry.Art };
            }

            return result;
        }

        private static string Resolve(
            string label,
            IReadOnlyDictionary<string, CategoryImageOverrideData> overrides,
            out IReadOnlyList<string> perLevel,
            CategoryArtChainMemo memo = null,
            Func<string, string> resolveOverridePath = null)
        {
            return CategoryArtChainResolver.Resolve(
                GameId,
                label,
                providerLabel: null,
                imageOverrides: overrides,
                resolveOverridePath: resolveOverridePath,
                probeEffectiveLabelDefault: false,
                memo: memo,
                ancestorArtPaths: out perLevel);
        }

        [TestMethod]
        public void Resolve_UsesTheNodesOwnOverride()
        {
            var art = Resolve("DLC::Winter", Overrides(("DLC::Winter", "winter.png")), out _);
            Assert.AreEqual("winter.png", art);
        }

        [TestMethod]
        public void Resolve_InheritsFromTheNearestAncestorWhenTheNodeHasNone()
        {
            var overrides = Overrides(("DLC", "dlc.png"));

            Assert.AreEqual("dlc.png", Resolve("DLC::Winter", overrides, out _));
            Assert.AreEqual("dlc.png", Resolve("DLC::Winter::Week1", overrides, out _));
        }

        [TestMethod]
        public void Resolve_PrefersTheNearestAncestorOverAMoreDistantOne()
        {
            var overrides = Overrides(("DLC", "dlc.png"), ("DLC::Winter", "winter.png"));

            Assert.AreEqual("winter.png", Resolve("DLC::Winter::Week1", overrides, out _));
        }

        [TestMethod]
        public void Resolve_PrefersTheNodesOwnArtOverAnyAncestor()
        {
            var overrides = Overrides(("DLC", "dlc.png"), ("DLC::Winter", "winter.png"));

            Assert.AreEqual("winter.png", Resolve("DLC::Winter", overrides, out _));
        }

        [TestMethod]
        public void Resolve_ReturnsNullWhenNothingInTheChainHasArt()
        {
            Assert.IsNull(Resolve("DLC::Winter", Overrides(("Other", "other.png")), out _));
        }

        [TestMethod]
        public void Resolve_IsUnchangedForAFlatLabel()
        {
            // A flat label has no ancestors, so the chain is exactly the override lookup it was.
            Assert.AreEqual("dlc.png", Resolve("DLC", Overrides(("DLC", "dlc.png")), out var perLevel));
            Assert.AreEqual(1, perLevel.Count);
            Assert.IsNull(Resolve("DLC", Overrides(("Other", "other.png")), out _));
        }

        [TestMethod]
        public void Resolve_ReportsArtPerLevelRootFirst()
        {
            var overrides = Overrides(("DLC", "dlc.png"), ("DLC::Winter::Week1", "week1.png"));

            Resolve("DLC::Winter::Week1", overrides, out var perLevel);

            // Index (depth - 1) is that level's own art: an aggregate row for "DLC" must find
            // dlc.png rather than the leaf's week1.png.
            CollectionAssert.AreEqual(new[] { "dlc.png", null, "week1.png" }, (string[])perLevel);
        }

        [TestMethod]
        public void Resolve_DoesNotLetADescendantsArtLeakUpIntoAnAncestorLevel()
        {
            Resolve("DLC::Winter", Overrides(("DLC::Winter", "winter.png")), out var perLevel);

            Assert.IsNull(perLevel[0], "the parent level has no art of its own");
            Assert.AreEqual("winter.png", perLevel[1]);
        }

        [TestMethod]
        public void Resolve_TrimsAndDropsBlankStoredValues()
        {
            Assert.AreEqual("winter.png", Resolve("DLC::Winter", Overrides(("DLC::Winter", "  winter.png  ")), out _));
            Assert.IsNull(Resolve("DLC::Winter", Overrides(("DLC::Winter", "   ")), out _));
        }

        [TestMethod]
        public void Resolve_ResolvesAnAncestorOncePerPassWhenGivenAMemo()
        {
            var overrides = Overrides(("DLC", "dlc.png"));
            var lookups = 0;
            Func<string, string> counting = value =>
            {
                lookups++;
                return CategoryArtChainResolver.NormalizeStoredValue(value);
            };

            var memo = new CategoryArtChainMemo();
            Resolve("DLC::Winter", overrides, out _, memo, counting);
            Resolve("DLC::Summer", overrides, out _, memo, counting);
            Resolve("DLC::Autumn", overrides, out _, memo, counting);

            Assert.AreEqual(1, lookups, "the shared 'DLC' ancestor must be resolved once for the pass");
        }

        [TestMethod]
        public void Resolve_ResolvesAnAncestorPerLeafWithoutAMemo()
        {
            var overrides = Overrides(("DLC", "dlc.png"));
            var lookups = 0;
            Func<string, string> counting = value =>
            {
                lookups++;
                return CategoryArtChainResolver.NormalizeStoredValue(value);
            };

            Resolve("DLC::Winter", overrides, out _, memo: null, resolveOverridePath: counting);
            Resolve("DLC::Summer", overrides, out _, memo: null, resolveOverridePath: counting);

            Assert.AreEqual(2, lookups, "this is the repeated work the memo exists to remove");
        }

        [TestMethod]
        public void Resolve_RoutesStoredValuesThroughTheCallersPathResolver()
        {
            var art = Resolve(
                "DLC",
                Overrides(("DLC", "stored.png")),
                out _,
                memo: null,
                resolveOverridePath: value => "resolved:" + value);

            Assert.AreEqual("resolved:stored.png", art);
        }
    }
}
