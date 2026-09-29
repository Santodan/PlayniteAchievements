using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace PlayniteAchievements.Services.CustomProviders
{
    public sealed class SvgGeometryImportException : Exception
    {
        public SvgGeometryImportException(string message, bool noDrawableShapes = false, Exception inner = null)
            : base(message, inner)
        {
            NoDrawableShapes = noDrawableShapes;
        }

        /// <summary>True when the document parsed but contained nothing that renders as a filled shape.</summary>
        public bool NoDrawableShapes { get; }
    }

    /// <summary>
    /// Converts an SVG document into a single WPF path-data string suitable for a single-tone
    /// provider icon (a <see cref="Geometry"/> filled with one brush). Shapes and their
    /// transforms are baked into one <see cref="PathGeometry"/>; fill and stroke colors are
    /// ignored. Stroke-only shapes are widened by their stroke width so line icons still render.
    /// Unsupported: <c>use</c>, <c>text</c>, gradients, clipping, percentage units.
    /// </summary>
    public static class SvgGeometryImporter
    {
        private static readonly Regex TransformFunctionRegex = new Regex(
            @"(?<name>[a-zA-Z]+)\s*\(\s*(?<args>[^)]*)\)",
            RegexOptions.Compiled);

        private static readonly Regex NumberRegex = new Regex(
            @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
            RegexOptions.Compiled);

        private static readonly HashSet<string> SkippedElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "defs", "clipPath", "mask", "symbol", "marker", "pattern", "linearGradient", "radialGradient",
            "filter", "metadata", "title", "desc", "style", "script", "text", "use", "image", "foreignObject"
        };

        private sealed class Style
        {
            public string Fill = "#000000";
            public string Stroke = "none";
            public double StrokeWidth = 1;
            public string StrokeLineCap = "butt";
            public string StrokeLineJoin = "miter";
            public FillRule FillRule = FillRule.Nonzero;
            public bool Hidden;

            public Style Clone() => (Style)MemberwiseClone();
        }

        public static string ImportFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new SvgGeometryImportException("SVG file not found.");
            }

            XDocument document;
            try
            {
                document = XDocument.Load(filePath);
            }
            catch (Exception ex)
            {
                throw new SvgGeometryImportException("The file is not a valid SVG document.", inner: ex);
            }

            return Import(document);
        }

        public static string ImportMarkup(string markup)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(markup ?? string.Empty);
            }
            catch (Exception ex)
            {
                throw new SvgGeometryImportException("The text is not a valid SVG document.", inner: ex);
            }

            return Import(document);
        }

        private static string Import(XDocument document)
        {
            var root = document?.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            {
                throw new SvgGeometryImportException("The document has no <svg> root element.");
            }

            var shapes = new List<Geometry>();
            var fillRules = new HashSet<FillRule>();
            Collect(root, Matrix.Identity, new Style(), shapes, fillRules, isRoot: true);

            if (shapes.Count == 0)
            {
                throw new SvgGeometryImportException("No drawable shapes were found.", noDrawableShapes: true);
            }

            Geometry combined;
            if (fillRules.Count > 1)
            {
                // Per-child fill rules do not survive flattening into one path, so a document that
                // mixes them is unioned region by region instead (curves flatten, regions stay right).
                combined = shapes[0];
                for (var i = 1; i < shapes.Count; i++)
                {
                    combined = Geometry.Combine(combined, shapes[i], GeometryCombineMode.Union, null);
                }
            }
            else
            {
                var group = new GeometryGroup { FillRule = fillRules.FirstOrDefault() };
                foreach (var shape in shapes)
                {
                    group.Children.Add(shape);
                }

                combined = group;
            }

            var path = PathGeometry.CreateFromGeometry(combined);
            if (path == null || path.Figures.Count == 0 || path.IsEmpty())
            {
                throw new SvgGeometryImportException("No drawable shapes were found.", noDrawableShapes: true);
            }

            // Invariant culture is load-bearing: the default ToString emits ';' separators under
            // comma-decimal cultures, which Geometry.Parse cannot read back.
            return path.ToString(CultureInfo.InvariantCulture);
        }

        private static void Collect(
            XElement element,
            Matrix parentMatrix,
            Style inherited,
            List<Geometry> shapes,
            HashSet<FillRule> fillRules,
            bool isRoot = false)
        {
            var name = element.Name.LocalName;
            if (SkippedElements.Contains(name))
            {
                return;
            }

            var style = ResolveStyle(element, inherited);
            if (style.Hidden)
            {
                return;
            }

            var matrix = isRoot
                ? parentMatrix
                : Matrix.Multiply(ParseTransform(element.Attribute("transform")?.Value), parentMatrix);

            Geometry shape = null;
            switch (name.ToLowerInvariant())
            {
                case "svg":
                case "g":
                case "a":
                case "switch":
                    foreach (var child in element.Elements())
                    {
                        Collect(child, matrix, style, shapes, fillRules);
                    }

                    return;
                case "path":
                    shape = BuildPath(element.Attribute("d")?.Value);
                    break;
                case "rect":
                    shape = BuildRect(element);
                    break;
                case "circle":
                    shape = BuildEllipse(element, "r", "r");
                    break;
                case "ellipse":
                    shape = BuildEllipse(element, "rx", "ry");
                    break;
                case "line":
                    shape = BuildLine(element);
                    break;
                case "polygon":
                    shape = BuildPolyline(element, close: true);
                    break;
                case "polyline":
                    shape = BuildPolyline(element, close: false);
                    break;
                default:
                    return;
            }

            if (shape == null)
            {
                return;
            }

            var hasFill = !IsNone(style.Fill);
            var hasStroke = !IsNone(style.Stroke) && style.StrokeWidth > 0;
            if (!hasFill && !hasStroke)
            {
                return;
            }

            if (!hasFill)
            {
                shape = Widen(shape, style);
                if (shape == null)
                {
                    return;
                }
            }
            else if (shape is StreamGeometry streamGeometry)
            {
                streamGeometry.FillRule = style.FillRule;
            }
            else if (shape is PathGeometry pathGeometry)
            {
                pathGeometry.FillRule = style.FillRule;
            }

            shape.Transform = new MatrixTransform(matrix);
            shapes.Add(shape);
            fillRules.Add(hasFill ? style.FillRule : FillRule.Nonzero);
        }

        private static Geometry Widen(Geometry shape, Style style)
        {
            try
            {
                var pen = new Pen(Brushes.Black, style.StrokeWidth)
                {
                    StartLineCap = ParseLineCap(style.StrokeLineCap),
                    EndLineCap = ParseLineCap(style.StrokeLineCap),
                    LineJoin = ParseLineJoin(style.StrokeLineJoin)
                };
                var widened = shape.GetWidenedPathGeometry(pen);
                widened.FillRule = FillRule.Nonzero;
                return widened.Figures.Count == 0 ? null : widened;
            }
            catch
            {
                return null;
            }
        }

        private static Geometry BuildPath(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            try
            {
                // Geometry.Parse returns a frozen StreamGeometry; clone before mutating fill rule
                // or transform.
                return Geometry.Parse(data.Trim()).Clone();
            }
            catch (Exception ex)
            {
                throw new SvgGeometryImportException("A <path> element has invalid path data.", inner: ex);
            }
        }

        private static Geometry BuildRect(XElement element)
        {
            if (!TryGetLength(element, "width", out var width) ||
                !TryGetLength(element, "height", out var height) ||
                width <= 0 ||
                height <= 0)
            {
                return null;
            }

            TryGetLength(element, "x", out var x);
            TryGetLength(element, "y", out var y);
            var hasRx = TryGetLength(element, "rx", out var rx);
            var hasRy = TryGetLength(element, "ry", out var ry);
            if (hasRx && !hasRy) ry = rx;
            if (hasRy && !hasRx) rx = ry;

            return new RectangleGeometry(new Rect(x, y, width, height), Math.Max(0, rx), Math.Max(0, ry));
        }

        private static Geometry BuildEllipse(XElement element, string rxName, string ryName)
        {
            if (!TryGetLength(element, rxName, out var rx) || rx <= 0)
            {
                return null;
            }

            if (!TryGetLength(element, ryName, out var ry) || ry <= 0)
            {
                return null;
            }

            TryGetLength(element, "cx", out var cx);
            TryGetLength(element, "cy", out var cy);
            return new EllipseGeometry(new Point(cx, cy), rx, ry);
        }

        private static Geometry BuildLine(XElement element)
        {
            TryGetLength(element, "x1", out var x1);
            TryGetLength(element, "y1", out var y1);
            TryGetLength(element, "x2", out var x2);
            TryGetLength(element, "y2", out var y2);
            if (x1 == x2 && y1 == y2)
            {
                return null;
            }

            return new LineGeometry(new Point(x1, y1), new Point(x2, y2));
        }

        private static Geometry BuildPolyline(XElement element, bool close)
        {
            var numbers = ParseNumbers(element.Attribute("points")?.Value);
            if (numbers.Count < 4)
            {
                return null;
            }

            var figure = new PathFigure
            {
                StartPoint = new Point(numbers[0], numbers[1]),
                IsClosed = close,
                IsFilled = true
            };
            var points = new List<Point>();
            for (var i = 2; i + 1 < numbers.Count; i += 2)
            {
                points.Add(new Point(numbers[i], numbers[i + 1]));
            }

            figure.Segments.Add(new PolyLineSegment(points, true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }

        private static Style ResolveStyle(XElement element, Style inherited)
        {
            var style = inherited.Clone();
            var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attributeName in new[] { "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "fill-rule", "display", "visibility" })
            {
                var value = element.Attribute(attributeName)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    declarations[attributeName] = value.Trim();
                }
            }

            var css = element.Attribute("style")?.Value;
            if (!string.IsNullOrWhiteSpace(css))
            {
                foreach (var declaration in css.Split(';'))
                {
                    var separator = declaration.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var key = declaration.Substring(0, separator).Trim();
                    var value = declaration.Substring(separator + 1).Trim();
                    if (key.Length > 0 && value.Length > 0)
                    {
                        declarations[key] = value;
                    }
                }
            }

            if (declarations.TryGetValue("fill", out var fill) && !IsInheritKeyword(fill)) style.Fill = fill;
            if (declarations.TryGetValue("stroke", out var stroke) && !IsInheritKeyword(stroke)) style.Stroke = stroke;
            if (declarations.TryGetValue("stroke-width", out var strokeWidth) && TryParseLength(strokeWidth, out var parsedWidth)) style.StrokeWidth = parsedWidth;
            if (declarations.TryGetValue("stroke-linecap", out var lineCap)) style.StrokeLineCap = lineCap;
            if (declarations.TryGetValue("stroke-linejoin", out var lineJoin)) style.StrokeLineJoin = lineJoin;
            if (declarations.TryGetValue("fill-rule", out var fillRule))
            {
                style.FillRule = string.Equals(fillRule, "evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.Nonzero;
            }

            if (declarations.TryGetValue("display", out var display) && string.Equals(display, "none", StringComparison.OrdinalIgnoreCase))
            {
                style.Hidden = true;
            }

            if (declarations.TryGetValue("visibility", out var visibility) &&
                (string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(visibility, "collapse", StringComparison.OrdinalIgnoreCase)))
            {
                style.Hidden = true;
            }

            return style;
        }

        private static bool IsInheritKeyword(string value) =>
            string.Equals(value, "inherit", StringComparison.OrdinalIgnoreCase);

        private static bool IsNone(string paint) =>
            string.IsNullOrWhiteSpace(paint) ||
            string.Equals(paint.Trim(), "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(paint.Trim(), "transparent", StringComparison.OrdinalIgnoreCase);

        private static PenLineCap ParseLineCap(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "round": return PenLineCap.Round;
                case "square": return PenLineCap.Square;
                default: return PenLineCap.Flat;
            }
        }

        private static PenLineJoin ParseLineJoin(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "round": return PenLineJoin.Round;
                case "bevel": return PenLineJoin.Bevel;
                default: return PenLineJoin.Miter;
            }
        }

        /// <summary>
        /// Parses an SVG transform list into a WPF row-vector matrix. Functions apply right to
        /// left to a point, so each function is pre-multiplied onto the running product.
        /// </summary>
        internal static Matrix ParseTransform(string transform)
        {
            var result = Matrix.Identity;
            if (string.IsNullOrWhiteSpace(transform))
            {
                return result;
            }

            foreach (Match match in TransformFunctionRegex.Matches(transform))
            {
                var args = ParseNumbers(match.Groups["args"].Value);
                var function = Matrix.Identity;
                switch (match.Groups["name"].Value.ToLowerInvariant())
                {
                    case "translate":
                        if (args.Count >= 1)
                        {
                            function = new Matrix(1, 0, 0, 1, args[0], args.Count >= 2 ? args[1] : 0);
                        }

                        break;
                    case "scale":
                        if (args.Count >= 1)
                        {
                            function = new Matrix(args[0], 0, 0, args.Count >= 2 ? args[1] : args[0], 0, 0);
                        }

                        break;
                    case "rotate":
                        if (args.Count >= 1)
                        {
                            function = Matrix.Identity;
                            if (args.Count >= 3)
                            {
                                function.RotateAt(args[0], args[1], args[2]);
                            }
                            else
                            {
                                function.Rotate(args[0]);
                            }
                        }

                        break;
                    case "skewx":
                        if (args.Count >= 1)
                        {
                            function = new Matrix(1, 0, Math.Tan(args[0] * Math.PI / 180.0), 1, 0, 0);
                        }

                        break;
                    case "skewy":
                        if (args.Count >= 1)
                        {
                            function = new Matrix(1, Math.Tan(args[0] * Math.PI / 180.0), 0, 1, 0, 0);
                        }

                        break;
                    case "matrix":
                        if (args.Count >= 6)
                        {
                            function = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                        }

                        break;
                }

                result = Matrix.Multiply(function, result);
            }

            return result;
        }

        private static bool TryGetLength(XElement element, string attributeName, out double value)
        {
            value = 0;
            var raw = element.Attribute(attributeName)?.Value;
            return !string.IsNullOrWhiteSpace(raw) && TryParseLength(raw, out value);
        }

        private static bool TryParseLength(string raw, out double value)
        {
            value = 0;
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0 || text.EndsWith("%", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var unit in new[] { "px", "pt", "pc", "mm", "cm", "in", "em", "ex" })
            {
                if (text.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                {
                    text = text.Substring(0, text.Length - unit.Length).Trim();
                    break;
                }
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static List<double> ParseNumbers(string text)
        {
            var numbers = new List<double>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return numbers;
            }

            foreach (Match match in NumberRegex.Matches(text))
            {
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    numbers.Add(number);
                }
            }

            return numbers;
        }
    }
}
