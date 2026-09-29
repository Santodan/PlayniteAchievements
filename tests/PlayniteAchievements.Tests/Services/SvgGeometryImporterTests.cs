using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.CustomProviders;
using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class SvgGeometryImporterTests
    {
        private const string SvgNs = "http://www.w3.org/2000/svg";

        private static Geometry Parse(string pathData) => Geometry.Parse(pathData);

        [TestMethod]
        public void ImportMarkup_PathOnly_RoundTripsThroughGeometryParse()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}' viewBox='0 0 24 24'><path d='M2 2h10v10H2z'/></svg>");

            var geometry = Parse(pathData);
            Assert.IsFalse(geometry.IsEmpty());
            AssertBounds(new Rect(2, 2, 10, 10), geometry.Bounds);
        }

        [TestMethod]
        public void ImportMarkup_TransformedGroup_BakesTranslateAndScale()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'><g transform='translate(100 50)'><rect x='0' y='0' width='10' height='10' transform='scale(2)'/></g></svg>");

            var bounds = Parse(pathData).Bounds;
            AssertBounds(new Rect(100, 50, 20, 20), bounds);
        }

        [TestMethod]
        public void ImportMarkup_Primitives_ProduceFilledShapes()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'>" +
                "<circle cx='5' cy='5' r='5'/>" +
                "<ellipse cx='30' cy='5' rx='10' ry='5'/>" +
                "<polygon points='0,20 10,20 5,30'/>" +
                "</svg>");

            var bounds = Parse(pathData).Bounds;
            Assert.AreEqual(0, bounds.Left, 0.01);
            Assert.AreEqual(0, bounds.Top, 0.01);
            Assert.AreEqual(40, bounds.Right, 0.01);
            Assert.AreEqual(30, bounds.Bottom, 0.01);
        }

        [TestMethod]
        public void ImportMarkup_StrokeOnlyLine_IsWidenedIntoAFilledShape()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'><line x1='0' y1='0' x2='10' y2='0' fill='none' stroke='#000' stroke-width='2'/></svg>");

            var geometry = Parse(pathData);
            Assert.IsFalse(geometry.IsEmpty());
            Assert.IsTrue(geometry.Bounds.Height >= 1.9, $"stroke width should widen the line; bounds were {geometry.Bounds}");
        }

        [TestMethod]
        public void ImportMarkup_EvenOddFillRule_IsPreserved()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'><path fill-rule='evenodd' d='M0 0h10v10H0z M2 2h6v6H2z'/></svg>");

            var geometry = (StreamGeometry)Parse(pathData);
            Assert.AreEqual(FillRule.EvenOdd, geometry.FillRule);
            Assert.IsFalse(pathData.StartsWith("F1", StringComparison.Ordinal));
        }

        [TestMethod]
        public void ImportMarkup_NonzeroDefault_EmitsNonzeroPrefix()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'><path d='M0 0h10v10H0z'/></svg>");

            Assert.IsTrue(pathData.StartsWith("F1", StringComparison.Ordinal), pathData);
        }

        [TestMethod]
        public void ImportMarkup_SkipsDefsAndHiddenElements()
        {
            var pathData = SvgGeometryImporter.ImportMarkup(
                $"<svg xmlns='{SvgNs}'>" +
                "<defs><path d='M100 100h50v50h-50z'/></defs>" +
                "<path d='M200 200h5v5h-5z' display='none'/>" +
                "<path d='M0 0h10v10H0z'/>" +
                "</svg>");

            AssertBounds(new Rect(0, 0, 10, 10), Parse(pathData).Bounds);
        }

        [TestMethod]
        public void ImportMarkup_NoShapes_ThrowsNoDrawableShapes()
        {
            try
            {
                SvgGeometryImporter.ImportMarkup($"<svg xmlns='{SvgNs}'><defs><path d='M0 0h1v1z'/></defs></svg>");
                Assert.Fail("expected an import exception");
            }
            catch (SvgGeometryImportException ex)
            {
                Assert.IsTrue(ex.NoDrawableShapes);
            }
        }

        [TestMethod]
        public void ImportMarkup_NotSvg_Throws()
        {
            try
            {
                SvgGeometryImporter.ImportMarkup("<html><body/></html>");
                Assert.Fail("expected an import exception");
            }
            catch (SvgGeometryImportException ex)
            {
                Assert.IsFalse(ex.NoDrawableShapes);
            }
        }

        [TestMethod]
        public void ImportMarkup_UnderCommaDecimalCulture_StaysParseable()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var pathData = SvgGeometryImporter.ImportMarkup(
                    $"<svg xmlns='{SvgNs}'><path d='M0.5 0.5h10.25v10.75H0.5z'/></svg>");

                Assert.IsFalse(pathData.Contains(";"), pathData);
                AssertBounds(new Rect(0.5, 0.5, 10.25, 10.75), Parse(pathData).Bounds);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void ParseTransform_RotateAboutPoint_MatchesWpfRotateAt()
        {
            var matrix = SvgGeometryImporter.ParseTransform("rotate(90 10 10)");
            var expected = Matrix.Identity;
            expected.RotateAt(90, 10, 10);

            var point = matrix.Transform(new Point(20, 10));
            var expectedPoint = expected.Transform(new Point(20, 10));
            Assert.AreEqual(expectedPoint.X, point.X, 0.0001);
            Assert.AreEqual(expectedPoint.Y, point.Y, 0.0001);
        }

        private static void AssertBounds(Rect expected, Rect actual)
        {
            Assert.AreEqual(expected.X, actual.X, 0.01, "X");
            Assert.AreEqual(expected.Y, actual.Y, 0.01, "Y");
            Assert.AreEqual(expected.Width, actual.Width, 0.01, "Width");
            Assert.AreEqual(expected.Height, actual.Height, 0.01, "Height");
        }
    }
}
