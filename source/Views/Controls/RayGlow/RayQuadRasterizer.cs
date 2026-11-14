using System;

namespace PlayniteAchievements.Views.Controls.RayGlow
{
    /// <summary>
    /// Fills copies of arrow quads into a premultiplied BGRA pixel buffer: each copy is the union
    /// of its quads at one colour, antialiased, drawn over the copies before it. It is the picture
    /// a nonzero fill of all of a copy's quads as one geometry gives.
    ///
    /// The quads of the wide copies each overlap several neighbours. Filled as one geometry by WPF
    /// that overlap was most of a card burst's cost (tools/capture-harness README.md, "The rays'
    /// cost"). Here the union's outline is found first: each quad edge loses the stretches that
    /// lie inside a quad near it in the loop, and what is left of all edges is the outline. Every
    /// outline piece then adds the signed area it sweeps to an accumulation buffer, and one pass
    /// along each row sums that into exact pixel coverage.
    ///
    /// Where two quads share a stretch of edge line, as the inner edges of neighbours along a
    /// straight side do, the quad earlier in the loop keeps it. Quads far apart in the loop that
    /// overlap anyway are not clipped against each other; their overlap is covered once, and only
    /// the antialiased pixels where their edges cross come out heavier.
    ///
    /// Not thread safe; one instance serves one caller at a time.
    /// </summary>
    internal sealed class RayQuadRasterizer
    {
        /// <summary>
        /// Where WPF's own antialiased fill of the same quads lands relative to exact coverage,
        /// in pixels: adding these to the offsets given to <see cref="Fill"/> had the least mean
        /// difference from a RenderTargetBitmap of the geometry at scales 1, 1.15 and 2, among
        /// sixteenths of a pixel on each axis.
        /// </summary>
        public const double WpfBiasX = 0.0625;

        /// <summary>The vertical counterpart of <see cref="WpfBiasX"/>.</summary>
        public const double WpfBiasY = 0.03125;

        // How far inside a quad's edge line a point still counts as on it, in pixels.
        private const double OnEdge = 1e-4;

        // Quads further apart in the loop than this are not clipped against each other.
        private const int MaxReach = 48;

        private float[] _area = new float[0];

        // Per row, the columns touched left of the middle and from the middle on. A loop around a
        // card touches both ends of most rows and nothing between them.
        private int[] _rowMin = new int[0];
        private int[] _rowMax = new int[0];
        private int[] _rightMin = new int[0];
        private int[] _rightMax = new int[0];
        private int _middle;
        private int _width;
        private int _height;
        private int _stride;

        // Per quad: its four points in pixels (8), its edges' inward unit normals and offsets
        // (12), and its bounding box (4).
        private double[] _points = new double[0];
        private double[] _planes = new double[0];
        private double[] _boxes = new double[0];
        private bool[] _usable = new bool[0];

        // Per quad: how many steps along the loop, either way, its box still meets another's.
        private int[] _span = new int[0];

        // Steps without a meeting box after which the search along the loop ends.
        private const int MissesToStop = 3;

        // The stretches of the edge being clipped that are still outline, as pairs of
        // parameters along it.
        private readonly double[] _kept = new double[64];
        private readonly double[] _next = new double[64];

        /// <summary>
        /// Sets the size of the buffer the copies are filled into, in pixels.
        /// </summary>
        public void Begin(int width, int height)
        {
            _width = Math.Max(0, width);
            _height = Math.Max(0, height);

            // Two columns of slack: an edge ending on the last column writes one past it.
            _stride = _width + 2;
            var needed = _stride * _height;
            if (_area.Length < needed)
            {
                _area = new float[needed];
            }

            if (_rowMin.Length < _height)
            {
                _rowMin = new int[_height];
                _rowMax = new int[_height];
                _rightMin = new int[_height];
                _rightMax = new int[_height];
            }

            _middle = _stride / 2;
            for (var y = 0; y < _height; y++)
            {
                _rowMin[y] = int.MaxValue;
                _rowMax[y] = -1;
                _rightMin[y] = int.MaxValue;
                _rightMax[y] = -1;
            }
        }

        /// <summary>
        /// Fills <paramref name="count"/> quads, given in their order around the loop, at
        /// <paramref name="alpha"/> of the given colour over what <paramref name="pixels"/>
        /// (premultiplied BGRA, <c>width</c> per row) holds. A quad's point p lands on pixel
        /// (p.X * scaleX + offsetX, p.Y * scaleY + offsetY); what falls outside the buffer is
        /// dropped.
        /// </summary>
        public void Fill(
            RayArrowLayout.RayArrowQuad[] quads, int count,
            double scaleX, double scaleY, double offsetX, double offsetY,
            byte alpha, byte red, byte green, byte blue, int[] pixels)
        {
            if (quads == null || pixels == null || _width <= 0 || _height <= 0 || alpha == 0 ||
                pixels.Length < _width * _height)
            {
                return;
            }

            var limit = Math.Min(count, quads.Length);
            Prepare(quads, limit, scaleX, scaleY, offsetX, offsetY);
            var reach = Math.Min(MaxReach, (limit - 1) / 2);
            for (var i = 0; i < limit; i++)
            {
                _span[i] = _usable[i] ? Span(i, limit, reach) : 0;
            }

            for (var i = 0; i < limit; i++)
            {
                if (!_usable[i])
                {
                    continue;
                }

                var p = i * 8;
                for (var edge = 0; edge < 4; edge++)
                {
                    var from = p + (edge * 2);
                    var to = p + (((edge + 1) & 3) * 2);
                    Outline(i, limit, _span[i], _points[from], _points[from + 1], _points[to], _points[to + 1]);
                }
            }

            Resolve(alpha, red, green, blue, pixels);
        }

        // Pixel points, wound one way for every quad, with each edge's inward normal.
        private void Prepare(
            RayArrowLayout.RayArrowQuad[] quads, int count,
            double scaleX, double scaleY, double offsetX, double offsetY)
        {
            if (_points.Length < count * 8)
            {
                _points = new double[count * 8];
                _planes = new double[count * 12];
                _boxes = new double[count * 4];
                _usable = new bool[count];
                _span = new int[count];
            }

            for (var i = 0; i < count; i++)
            {
                var quad = quads[i];
                var p = i * 8;
                _points[p] = (quad.BaseLeft.X * scaleX) + offsetX;
                _points[p + 1] = (quad.BaseLeft.Y * scaleY) + offsetY;
                _points[p + 2] = (quad.TipLeft.X * scaleX) + offsetX;
                _points[p + 3] = (quad.TipLeft.Y * scaleY) + offsetY;
                _points[p + 4] = (quad.TipRight.X * scaleX) + offsetX;
                _points[p + 5] = (quad.TipRight.Y * scaleY) + offsetY;
                _points[p + 6] = (quad.BaseRight.X * scaleX) + offsetX;
                _points[p + 7] = (quad.BaseRight.Y * scaleY) + offsetY;

                var doubled = 0d;
                var sum = 0d;
                for (var edge = 0; edge < 4; edge++)
                {
                    var a = p + (edge * 2);
                    var b = p + (((edge + 1) & 3) * 2);
                    doubled += (_points[a] * _points[b + 1]) - (_points[b] * _points[a + 1]);
                    sum += _points[a] + _points[a + 1];
                }

                _usable[i] = !double.IsNaN(sum) && !double.IsInfinity(sum) && Math.Abs(doubled) > 1e-9;
                if (!_usable[i])
                {
                    continue;
                }

                if (doubled < 0)
                {
                    // Swap the second and fourth points, which reverses the winding.
                    var x = _points[p + 2];
                    var y = _points[p + 3];
                    _points[p + 2] = _points[p + 6];
                    _points[p + 3] = _points[p + 7];
                    _points[p + 6] = x;
                    _points[p + 7] = y;
                }

                var box = i * 4;
                _boxes[box] = Math.Min(Math.Min(_points[p], _points[p + 2]), Math.Min(_points[p + 4], _points[p + 6]));
                _boxes[box + 1] = Math.Min(Math.Min(_points[p + 1], _points[p + 3]), Math.Min(_points[p + 5], _points[p + 7]));
                _boxes[box + 2] = Math.Max(Math.Max(_points[p], _points[p + 2]), Math.Max(_points[p + 4], _points[p + 6]));
                _boxes[box + 3] = Math.Max(Math.Max(_points[p + 1], _points[p + 3]), Math.Max(_points[p + 5], _points[p + 7]));

                for (var edge = 0; edge < 4; edge++)
                {
                    var a = p + (edge * 2);
                    var b = p + (((edge + 1) & 3) * 2);
                    var dx = _points[b] - _points[a];
                    var dy = _points[b + 1] - _points[a + 1];
                    var length = Math.Sqrt((dx * dx) + (dy * dy));
                    var plane = (i * 12) + (edge * 3);
                    if (length < 1e-9)
                    {
                        // No line to be on one side of: every point passes.
                        _planes[plane] = 0;
                        _planes[plane + 1] = 0;
                        _planes[plane + 2] = 1;
                        continue;
                    }

                    // Positive winding in these coordinates has its inside on the left of travel.
                    var nx = -dy / length;
                    var ny = dx / length;
                    _planes[plane] = nx;
                    _planes[plane + 1] = ny;
                    _planes[plane + 2] = -((nx * _points[a]) + (ny * _points[a + 1]));
                }
            }
        }

        // The furthest step along the loop, either way, at which a quad's box meets quad i's,
        // looking no further than a few steps past the last that did.
        private int Span(int i, int count, int reach)
        {
            var box = i * 4;
            var span = 0;
            var misses = 0;
            for (var step = 1; step <= reach && misses < MissesToStop; step++)
            {
                var met = false;
                for (var side = 0; side < 2; side++)
                {
                    var j = side == 0 ? i + step : i - step;
                    if (j >= count)
                    {
                        j -= count;
                    }
                    else if (j < 0)
                    {
                        j += count;
                    }

                    var other = j * 4;
                    if (_usable[j] && _boxes[other] <= _boxes[box + 2] && _boxes[other + 2] >= _boxes[box] &&
                        _boxes[other + 1] <= _boxes[box + 3] && _boxes[other + 3] >= _boxes[box + 1])
                    {
                        met = true;
                    }
                }

                if (met)
                {
                    span = step;
                    misses = 0;
                }
                else
                {
                    misses++;
                }
            }

            return span;
        }

        // Adds to the accumulation buffer the stretches of one of quad i's edges that no quad
        // near it in the loop covers.
        private void Outline(int i, int count, int reach, double x0, double y0, double x1, double y1)
        {
            var kept = 2;
            _kept[0] = 0d;
            _kept[1] = 1d;
            var left = Math.Min(x0, x1);
            var right = Math.Max(x0, x1);
            var top = Math.Min(y0, y1);
            var bottom = Math.Max(y0, y1);
            for (var step = 1; step <= reach && kept > 0; step++)
            {
                for (var side = 0; side < 2 && kept > 0; side++)
                {
                    var j = side == 0 ? i + step : i - step;
                    if (j >= count)
                    {
                        j -= count;
                    }
                    else if (j < 0)
                    {
                        j += count;
                    }

                    if (j == i || !_usable[j] || (side == 1 && (2 * step) == count))
                    {
                        continue;
                    }

                    var box = j * 4;
                    if (_boxes[box] > right || _boxes[box + 2] < left || _boxes[box + 1] > bottom || _boxes[box + 3] < top)
                    {
                        continue;
                    }

                    // A shared edge line belongs to the earlier quad: the later one counts it
                    // as covered, the earlier one does not.
                    var threshold = j < i ? -OnEdge : OnEdge;
                    var enter = 0d;
                    var exit = 1d;
                    var plane = j * 12;
                    for (var edge = 0; edge < 4 && enter < exit; edge++, plane += 3)
                    {
                        var d0 = (_planes[plane] * x0) + (_planes[plane + 1] * y0) + _planes[plane + 2] - threshold;
                        var d1 = (_planes[plane] * x1) + (_planes[plane + 1] * y1) + _planes[plane + 2] - threshold;
                        if (d0 < 0 && d1 < 0)
                        {
                            enter = 1d;
                            exit = 0d;
                        }
                        else if (d0 < 0)
                        {
                            enter = Math.Max(enter, d0 / (d0 - d1));
                        }
                        else if (d1 < 0)
                        {
                            exit = Math.Min(exit, d0 / (d0 - d1));
                        }
                    }

                    if (enter >= exit)
                    {
                        continue;
                    }

                    // Take [enter, exit] out of every kept stretch.
                    var written = 0;
                    for (var k = 0; k < kept; k += 2)
                    {
                        var a = _kept[k];
                        var b = _kept[k + 1];
                        if (exit <= a || enter >= b)
                        {
                            if (written + 2 <= _next.Length)
                            {
                                _next[written++] = a;
                                _next[written++] = b;
                            }

                            continue;
                        }

                        if (enter > a && written + 2 <= _next.Length)
                        {
                            _next[written++] = a;
                            _next[written++] = enter;
                        }

                        if (exit < b && written + 2 <= _next.Length)
                        {
                            _next[written++] = exit;
                            _next[written++] = b;
                        }
                    }

                    Array.Copy(_next, _kept, written);
                    kept = written;
                }
            }

            var dx = x1 - x0;
            var dy = y1 - y0;
            for (var k = 0; k < kept; k += 2)
            {
                var a = _kept[k];
                var b = _kept[k + 1];
                if (b - a <= 1e-12)
                {
                    continue;
                }

                Edge(
                    (float)(x0 + (dx * a)), (float)(y0 + (dy * a)),
                    (float)(x0 + (dx * b)), (float)(y0 + (dy * b)));
            }
        }

        // Adds the signed area an edge sweeps to its right, row by row: the edge's own pixels get
        // the part of the row's height the edge leaves to their right, and the pixel after them
        // the rest, so the running sum along a row is the coverage.
        private void Edge(float x0, float y0, float x1, float y1)
        {
            if (y0 == y1)
            {
                return;
            }

            var direction = 1f;
            if (y0 > y1)
            {
                direction = -1f;
                var swap = x0; x0 = x1; x1 = swap;
                swap = y0; y0 = y1; y1 = swap;
            }

            if (y1 <= 0 || y0 >= _height)
            {
                return;
            }

            var slope = (x1 - x0) / (y1 - y0);
            var x = x0;
            if (y0 < 0)
            {
                x -= y0 * slope;
                y0 = 0;
            }

            var lastRow = Math.Min(_height, (int)Math.Ceiling(y1));
            for (var row = (int)y0; row < lastRow; row++)
            {
                var rowTop = Math.Max(row, y0);
                var rowBottom = Math.Min(row + 1f, y1);
                var rowHeight = rowBottom - rowTop;
                if (rowHeight <= 0)
                {
                    continue;
                }

                var xNext = x + (slope * rowHeight);
                var signed = rowHeight * direction;
                var left = x < xNext ? x : xNext;
                var right = x < xNext ? xNext : x;
                x = xNext;

                // Outside the buffer on the left an edge still covers everything to its right;
                // outside on the right it covers nothing in the buffer.
                if (left >= _width)
                {
                    continue;
                }

                if (right <= 0)
                {
                    Add(row, 0, signed);
                    continue;
                }

                if (left < 0 || right > _width)
                {
                    // Partly outside: the edge's mean position stands for it.
                    var mid = Math.Max(0f, Math.Min(_width, 0.5f * (left + right)));
                    var cell = Math.Min(_width - 1, (int)mid);
                    var inside = mid - cell;
                    Add(row, cell, signed * (1f - inside));
                    Add(row, cell + 1, signed * inside);
                    continue;
                }

                var firstFloor = (float)Math.Floor(left);
                var first = (int)firstFloor;
                var lastCeil = (float)Math.Ceiling(right);
                var last = (int)lastCeil;
                if (last <= first + 1)
                {
                    var into = (0.5f * (left + right)) - firstFloor;
                    Add(row, first, signed * (1f - into));
                    Add(row, first + 1, signed * into);
                    continue;
                }

                var perPixel = 1f / (right - left);
                var enter = left - firstFloor;
                var firstArea = 0.5f * perPixel * (1f - enter) * (1f - enter);
                var exit = right - lastCeil + 1f;
                var lastArea = 0.5f * perPixel * exit * exit;
                Add(row, first, signed * firstArea);
                if (last == first + 2)
                {
                    Add(row, first + 1, signed * (1f - firstArea - lastArea));
                }
                else
                {
                    var second = perPixel * (1.5f - enter);
                    Add(row, first + 1, signed * (second - firstArea));
                    for (var column = first + 2; column < last - 1; column++)
                    {
                        Add(row, column, signed * perPixel);
                    }

                    var beforeLast = second + ((last - first - 3) * perPixel);
                    Add(row, last - 1, signed * (1f - beforeLast - lastArea));
                }

                Add(row, last, signed * lastArea);
            }
        }

        private void Add(int row, int column, float value)
        {
            _area[(row * _stride) + column] += value;
            if (column < _middle)
            {
                if (column < _rowMin[row])
                {
                    _rowMin[row] = column;
                }

                if (column > _rowMax[row])
                {
                    _rowMax[row] = column;
                }
            }
            else
            {
                if (column < _rightMin[row])
                {
                    _rightMin[row] = column;
                }

                if (column > _rightMax[row])
                {
                    _rightMax[row] = column;
                }
            }
        }

        // Sums each touched row into coverage, blends the colour over the pixels by it and
        // leaves the accumulation buffer empty for the next copy.
        private void Resolve(byte alpha, byte red, byte green, byte blue, int[] pixels)
        {
            for (var row = 0; row < _height; row++)
            {
                var leftTo = _rowMax[row];
                var rightTo = _rightMax[row];
                if (leftTo < 0 && rightTo < 0)
                {
                    continue;
                }

                var start = row * _stride;
                var pixelRow = row * _width;
                var running = 0f;
                var column = leftTo >= 0 ? _rowMin[row] : _rightMin[row];
                var touchedTo = rightTo >= 0 ? rightTo : leftTo;
                while (column < _width)
                {
                    if (column <= touchedTo)
                    {
                        running += _area[start + column];
                        _area[start + column] = 0f;
                    }

                    var coverage = running < 0 ? -running : running;
                    if (coverage <= 0.002f)
                    {
                        if (column > touchedTo)
                        {
                            break;
                        }

                        // Nothing open here: past the left stretch, the next thing is the right one.
                        if (column >= leftTo && rightTo >= 0 && column < _rightMin[row])
                        {
                            running = 0f;
                            column = _rightMin[row];
                            continue;
                        }

                        column++;
                        continue;
                    }

                    if (coverage > 1f)
                    {
                        coverage = 1f;
                    }

                    var source = (int)((coverage * alpha) + 0.5f);
                    if (source > 0)
                    {
                        var under = pixels[pixelRow + column];
                        var keep = 255 - source;
                        var a = Scale(255, source) + Scale((under >> 24) & 0xFF, keep);
                        var r = Scale(red, source) + Scale((under >> 16) & 0xFF, keep);
                        var g = Scale(green, source) + Scale((under >> 8) & 0xFF, keep);
                        var b = Scale(blue, source) + Scale(under & 0xFF, keep);
                        pixels[pixelRow + column] = (a << 24) | (r << 16) | (g << 8) | b;
                    }

                    column++;
                }

                // The slack columns past the last pixel.
                for (var slack = _width; slack <= touchedTo; slack++)
                {
                    _area[start + slack] = 0f;
                }

                _rowMin[row] = int.MaxValue;
                _rowMax[row] = -1;
                _rightMin[row] = int.MaxValue;
                _rightMax[row] = -1;
            }
        }

        private static int Scale(int value, int by)
        {
            var product = (value * by) + 128;
            return (product + (product >> 8)) >> 8;
        }
    }
}
