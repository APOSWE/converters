﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace IxMilia.Converters
{
    public struct DxfToSvgConverterOptions
    {
        public ConverterDxfRect DxfRect { get; }
        public ConverterSvgRect SvgRect { get; }
        public string SvgId { get; }

        private Func<string, Task<string>> _imageHrefResolver;

        public DxfToSvgConverterOptions(ConverterDxfRect dxfRect, ConverterSvgRect svgRect, string svgId = null, Func<string, Task<string>> imageHrefResolver = null)
        {
            DxfRect = dxfRect;
            SvgRect = svgRect;
            SvgId = svgId;
            _imageHrefResolver = imageHrefResolver ?? (href => Task.FromResult(href));
        }

        public Task<string> ResolveImageHrefAsync(string path) => _imageHrefResolver(path);

        public static Func<string, Task<string>> CreateDataUriResolver(Func<string, Task<byte[]>> dataResolver)
        {
            return async path =>
            {
                string mimeType;
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".jpg":
                    case ".jpeg":
                        mimeType = "image/jpeg";
                        break;
                    case ".png":
                        mimeType = "image/png";
                        break;
                    default:
                        mimeType = "image/unknown";
                        break;
                }

                var data = await dataResolver(path);
                var base64 = Convert.ToBase64String(data);
                return $"data:{mimeType};base64,{base64}";
            };
        }
    }

    public class DxfToSvgConverter : IConverter<DxfFile, XElement, DxfToSvgConverterOptions>
    {
        public static XNamespace Xmlns = "http://www.w3.org/2000/svg";

        public async Task<XElement> Convert(DxfFile file, DxfToSvgConverterOptions options)
        {
            // adapted from https://github.com/ixmilia/bcad/blob/main/src/IxMilia.BCad.FileHandlers/Plotting/Svg/SvgPlotter.cs
            var worldGroup = new XElement(Xmlns + "g");
            var autoColor = DxfColor.FromIndex(0);

            // do images first so lines and text appear on top...
            foreach (var layer in file.Layers.OrderBy(layer => layer.Name))
            {
                var addedImage = false;
                worldGroup.Add(new XComment($" layer '{layer.Name}' images "));
                var layerGroup = new XElement(Xmlns + "g",
                    new XAttribute("stroke", (layer.Color ?? autoColor).ToRGBString()),
                    new XAttribute("fill", (layer.Color ?? autoColor).ToRGBString()),
                    new XAttribute("class", $"dxf-layer {layer.Name}"));
                foreach (var entity in file.Entities.OfType<DxfImage>().Where(i => i.Layer == layer.Name))
                {
                    var element = await entity.ToXElement(options);
                    if (element != null)
                    {
                        layerGroup.Add(element);
                        addedImage = true;
                    }
                }

                if (addedImage)
                {
                    worldGroup.Add(layerGroup);
                }
            }

            // ...now do lines and text
            foreach (var layer in file.Layers.OrderBy(layer => layer.Name))
            {
                worldGroup.Add(new XComment($" layer '{layer.Name}' "));
                var layerGroup = new XElement(Xmlns + "g",
                    new XAttribute("stroke", (layer.Color ?? autoColor).ToRGBString()),
                    new XAttribute("fill", (layer.Color ?? autoColor).ToRGBString()),
                    new XAttribute("class", $"dxf-layer {layer.Name}"));
                foreach (var entity in file.Entities.Where(entity => entity.Layer == layer.Name && entity.EntityType != DxfEntityType.Image))
                {
                    var element = await entity.ToXElement(options);
                    if (element != null)
                    {
                        layerGroup.Add(element);
                    }
                }

                worldGroup.Add(layerGroup);
            }

            var dxfAspectRatio = options.DxfRect.Width / options.DxfRect.Height;
            var svgAspectRatio = options.SvgRect.Width / options.SvgRect.Height;
            var scale = svgAspectRatio < dxfAspectRatio
                ? options.SvgRect.Width / options.DxfRect.Width
                : options.SvgRect.Height / options.DxfRect.Height;

            var root = new XElement(Xmlns + "svg",
                new XAttribute("width", options.SvgRect.Width.ToDisplayString()),
                new XAttribute("height", options.SvgRect.Height.ToDisplayString()),
                new XAttribute("viewBox", $"0 0 {options.SvgRect.Width.ToDisplayString()} {options.SvgRect.Height.ToDisplayString()}"),
                new XAttribute("version", "1.1"),
                new XAttribute("class", "dxf-drawing"),
                new XComment(" this group corrects for the y-axis going in different directions "),
                new XElement(Xmlns + "g",
                    new XAttribute("transform", $"translate(0 {options.SvgRect.Height.ToDisplayString()}) scale(1 -1)"),
                    new XComment(" this group handles display panning "),
                    new XElement(Xmlns + "g",
                        new XAttribute("transform", "translate(0 0)"),
                        new XAttribute("class", "svg-translate"),
                        new XComment(" this group handles display scaling "),
                        new XElement(Xmlns + "g",
                            new XAttribute("transform", $"scale({scale.ToDisplayString()} {scale.ToDisplayString()})"),
                            new XAttribute("class", "svg-scale"),
                            new XComment(" this group handles initial translation offset "),
                            new XElement(Xmlns + "g",
                                new XAttribute("transform", $"translate({(-options.DxfRect.Left).ToDisplayString()} {(-options.DxfRect.Bottom).ToDisplayString()})"),
                                worldGroup)))));

            var layerNames = file.Layers.OrderBy(layer => layer.Name).Select(layer => layer.Name);
            root = TransformToHtmlDiv(root, options.SvgId, layerNames, -options.DxfRect.Left, -options.DxfRect.Bottom, scale, scale);
            return root;
        }

        private static XElement TransformToHtmlDiv(XElement svg, string svgId, IEnumerable<string> layerNames, double defaultXTranslate, double defaultYTranslate, double defaultXScale, double defaultYScale)
        {
            if (string.IsNullOrWhiteSpace(svgId))
            {
                return svg;
            }

            var div = new XElement("div",
                new XAttribute("id", svgId),
                new XElement("style", GetCss()),
                new XElement("details",
                    new XElement("summary", "Controls"),
                    new XElement("button",
                        new XAttribute("class", "button-zoom-out"),
                        "Zoom out"),
                    new XElement("button",
                        new XAttribute("class", "button-zoom-in"),
                        "Zoom in"),
                    new XElement("button",
                        new XAttribute("class", "button-pan-left"),
                        "Pan left"),
                    new XElement("button",
                        new XAttribute("class", "button-pan-right"),
                        "Pan right"),
                    new XElement("button",
                        new XAttribute("class", "button-pan-up"),
                        "Pan up"),
                    new XElement("button",
                        new XAttribute("class", "button-pan-down"),
                        "Pan down"),
                    new XElement("button",
                        new XAttribute("class", "button-reset-view"),
                        "Reset")),
                    new XElement("details",
                        new XElement("summary", "Layers"),
                        new XElement("div",
                            new XAttribute("class", "layers-control"))
                        ),
                svg,
                new XElement("script",
                    new XAttribute("type", "text/javascript"),
                    new XRawText(GetJavascriptControls(svgId, layerNames, defaultXTranslate, defaultYTranslate, defaultXScale, defaultYScale))));
            return div;
        }

        private static string GetJavascriptControls(string svgId, IEnumerable<string> layerNames, double defaultXTranslate, double defaultYTranslate, double defaultXScale, double defaultYScale)
        {
            var assembly = typeof(DxfToSvgConverter).GetTypeInfo().Assembly;
            using (var jsStream = assembly.GetManifestResourceStream("IxMilia.Converters.SvgJavascriptControls.js"))
            using (var streamReader = new StreamReader(jsStream))
            {
                var contents = Environment.NewLine + streamReader.ReadToEnd();
                contents = contents
                    .Replace("$DRAWING-ID$", svgId)
                    .Replace("$LAYER-NAMES$", $"[{string.Join(", ", layerNames.Select(layer => $"\"{layer}\""))}]")
                    .Replace("$DEFAULT-X-TRANSLATE$", defaultXTranslate.ToDisplayString())
                    .Replace("$DEFAULT-Y-TRANSLATE$", defaultYTranslate.ToDisplayString())
                    .Replace("$DEFAULT-X-SCALE$", defaultXScale.ToDisplayString())
                    .Replace("$DEFAULT-Y-SCALE$", defaultYScale.ToDisplayString());
                return contents;
            }
        }

        private static string GetCss()
        {
            var assembly = typeof(DxfToSvgConverter).GetTypeInfo().Assembly;
            using (var jsStream = assembly.GetManifestResourceStream("IxMilia.Converters.SvgStyles.css"))
            using (var streamReader = new StreamReader(jsStream))
            {
                var contents = Environment.NewLine + streamReader.ReadToEnd();
                // perform replacements when necessary
                return contents;
            }
        }

        private class XRawText : XText
        {
            public XRawText(string text)
                : base(text)
            {
            }

            public override void WriteTo(XmlWriter writer)
            {
                writer.WriteRaw(Value);
            }
        }
    }

    public static class SvgExtensions
    {
        public static void SaveTo(this XElement document, Stream output)
        {
            var settings = new XmlWriterSettings()
            {
                Indent = true,
                IndentChars = "  "
            };
            using (var writer = XmlWriter.Create(output, settings))
            {
                document.WriteTo(writer);
            }
        }

        public static void SaveTo(this XElement document, string filePath)
        {
            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                document.SaveTo(fileStream);
            }
        }

        public static string ToRGBString(this DxfColor color)
        {
            var intValue = color.IsIndex
                ? color.ToRGB()
                : 0; // fall back to black
            var r = (intValue >> 16) & 0xFF;
            var g = (intValue >> 8) & 0xFF;
            var b = intValue & 0xFF;
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        internal static string ToDisplayString(this double value)
        {
            return value.ToString("0.0##############", CultureInfo.InvariantCulture);
        }

        public static async Task<XElement> ToXElement(this DxfEntity entity, DxfToSvgConverterOptions options)
        {
            // elements are simply flattened in the z plane; the world transform in the main function handles the rest
            switch (entity)
            {
                case DxfArc arc:
                    return arc.ToXElement();
                case DxfCircle circle:
                    return circle.ToXElement();
                case DxfEllipse ellipse:
                    return ellipse.ToXElement();
                case DxfImage image:
                    return await image.ToXElement(options);
                case DxfLine line:
                    return line.ToXElement();
                case DxfLwPolyline lwPolyline:
                    return lwPolyline.ToXElement();
                case DxfPolyline polyline:
                    return polyline.ToXElement();
                case DxfInsert insert:
                    return await insert.ToXElement(options);
                case DxfSpline spline:
                    return spline.ToXElement();
                default:
                    return null;
            }
        }

        public static XElement ToXElement(this DxfArc arc)
        {
            var path = arc.GetSvgPath();
            return new XElement(DxfToSvgConverter.Xmlns + "path",
                new XAttribute("d", path.ToString()),
                new XAttribute("fill-opacity", 0))
                .AddStroke(arc.Color)
                .AddStrokeWidth(arc.Thickness)
                .AddVectorEffect();
        }

        public static XElement ToXElement(this DxfCircle circle)
        {
            return new XElement(DxfToSvgConverter.Xmlns + "ellipse",
                new XAttribute("cx", circle.Center.X.ToDisplayString()),
                new XAttribute("cy", circle.Center.Y.ToDisplayString()),
                new XAttribute("rx", circle.Radius.ToDisplayString()),
                new XAttribute("ry", circle.Radius.ToDisplayString()),
                new XAttribute("fill-opacity", 0))
                .AddStroke(circle.Color)
                .AddStrokeWidth(circle.Thickness)
                .AddVectorEffect();
        }

        public static XElement ToXElement(this DxfEllipse ellipse)
        {
            XElement baseShape;
            if (ellipse.StartParameter.IsCloseTo(0.0) && ellipse.EndParameter.IsCloseTo(Math.PI * 2.0))
            {
                baseShape = new XElement(DxfToSvgConverter.Xmlns + "ellipse",
                    new XAttribute("cx", ellipse.Center.X.ToDisplayString()),
                    new XAttribute("cy", ellipse.Center.Y.ToDisplayString()),
                    new XAttribute("rx", ellipse.MajorAxis.Length.ToDisplayString()),
                    new XAttribute("ry", ellipse.MinorAxis().Length.ToDisplayString()));
            }
            else
            {
                var path = ellipse.GetSvgPath();
                baseShape = new XElement(DxfToSvgConverter.Xmlns + "path",
                    new XAttribute("d", path.ToString()));
            }

            baseShape.Add(new XAttribute("fill-opacity", 0));
            return baseShape
                .AddStroke(ellipse.Color)
                .AddStrokeWidth(1.0)
                .AddVectorEffect();
        }

        public static async Task<XElement> ToXElement(this DxfImage image, DxfToSvgConverterOptions options)
        {
            var imageHref = await options.ResolveImageHrefAsync(image.ImageDefinition.FilePath);
            var imageWidth = image.UVector.Length * image.ImageSize.X;
            var imageHeight = image.VVector.Length * image.ImageSize.Y;
            var radians = Math.Atan2(image.UVector.Y, image.UVector.X);
            var upVector = new DxfVector(-Math.Sin(radians), Math.Cos(radians), 0.0) * imageHeight;
            var displayRotationDegrees = -radians * 180.0 / Math.PI;
            var topLeftDxf = image.Location + upVector;
            var insertLocation = topLeftDxf;
            return new XElement(DxfToSvgConverter.Xmlns + "image",
                new XAttribute("href", imageHref),
                new XAttribute("width", imageWidth.ToDisplayString()),
                new XAttribute("height", imageHeight.ToDisplayString()),
                new XAttribute("transform", $"translate({insertLocation.X.ToDisplayString()} {insertLocation.Y.ToDisplayString()}) scale(1 -1) rotate({displayRotationDegrees.ToDisplayString()})"))
                .AddStroke(image.Color)
                .AddVectorEffect();
        }

        public static XElement ToXElement(this DxfLine line)
        {
            return new XElement(DxfToSvgConverter.Xmlns + "line",
                new XAttribute("x1", line.P1.X.ToDisplayString()),
                new XAttribute("y1", line.P1.Y.ToDisplayString()),
                new XAttribute("x2", line.P2.X.ToDisplayString()),
                new XAttribute("y2", line.P2.Y.ToDisplayString()))
                .AddStroke(line.Color)
                .AddStrokeWidth(line.Thickness)
                .AddVectorEffect();
        }

        public static XElement ToXElement(this DxfLwPolyline poly)
        {
            var path = poly.GetSvgPath();
            return new XElement(DxfToSvgConverter.Xmlns + "path",
                new XAttribute("d", path.ToString()),
                new XAttribute("fill-opacity", 0))
                .AddStroke(poly.Color)
                .AddStrokeWidth(1.0)
                .AddVectorEffect();
        }

        public static XElement ToXElement(this DxfPolyline poly)
        {
            var path = poly.GetSvgPath();
            return new XElement(DxfToSvgConverter.Xmlns + "path",
                new XAttribute("d", path.ToString()),
                new XAttribute("fill-opacity", 0))
                .AddStroke(poly.Color)
                .AddStrokeWidth(1.0)
                .AddVectorEffect();
        }

        public static async Task<XElement> ToXElement(this DxfInsert insert, DxfToSvgConverterOptions options)
        {
            var g = new XElement(DxfToSvgConverter.Xmlns + "g",
                new XAttribute("class", $"dxf-insert {insert.Name}"),
                new XAttribute("transform", $"translate({insert.Location.X.ToDisplayString()} {insert.Location.Y.ToDisplayString()}) scale({insert.XScaleFactor.ToDisplayString()} {insert.YScaleFactor.ToDisplayString()})"));
            foreach (var blockEntity in insert.Entities)
            {
                g.Add(await blockEntity.ToXElement(options));
            }

            return g;
        }

        public static XElement ToXElement(this DxfSpline spline)
        {
            var spline2 = new Spline2(
                spline.DegreeOfCurve,
                spline.ControlPoints.Select(p => new SplinePoint2(p.Point.X, p.Point.Y)),
                spline.KnotValues);
            var beziers = spline2.ToBeziers();
            var path = beziers.GetSvgPath();
            return new XElement(DxfToSvgConverter.Xmlns + "path",
                new XAttribute("d", path.ToString()),
                new XAttribute("fill-opacity", 0))
                .AddStroke(spline.Color)
                .AddStrokeWidth(1.0)
                .AddVectorEffect();
        }

        private static SvgPathSegment FromPolylineVertices(DxfLwPolylineVertex last, DxfLwPolylineVertex next)
        {
            return FromPolylineVertices(last.X, last.Y, last.Bulge, next.X, next.Y);
        }

        private static SvgPathSegment FromPolylineVertices(DxfVertex last, DxfVertex next)
        {
            return FromPolylineVertices(last.Location.X, last.Location.Y, last.Bulge, next.Location.X, next.Location.Y);
        }

        private static SvgPathSegment FromPolylineVertices(double lastX, double lastY, double lastBulge, double nextX, double nextY)
        {
            var dx = nextX - lastX;
            var dy = nextY - lastY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (lastBulge.IsCloseTo(0.0) || dist.IsCloseTo(1.0e-10))
            {
                // line or a really short arc
                return new SvgLineToPath(nextX, nextY);
            }

            // given the following diagram:
            //
            //                p1
            //               -)
            //            -  |  )
            //        -      |    )
            //    -          |     )
            // O ------------|C----T
            //    -          |     )
            //        -      |    )
            //            -  |  )
            //               -)
            //               p2
            //
            // where O is the center of the circle, C is the midpoint between p1 and p2, calculate
            // the hypotenuse of the triangle Op1C to get the radius

            var includedAngle = Math.Atan(Math.Abs(lastBulge)) * 4.0;
            var isLargeArc = includedAngle > Math.PI;
            var isCounterClockwise = lastBulge > 0.0;

            // find radius
            var oppositeLength = dist / 2.0;
            var radius = oppositeLength / Math.Sin(includedAngle / 2.0);

            return new SvgArcToPath(radius, radius, 0.0, isLargeArc, isCounterClockwise, nextX, nextY);
        }

        internal static SvgPath GetSvgPath(this DxfArc arc)
        {
            var startAngle = arc.StartAngle * Math.PI / 180.0;
            var endAngle = arc.EndAngle * Math.PI / 180.0;
            return SvgPath.FromEllipse(arc.Center.X, arc.Center.Y, arc.Radius, 0.0, 1.0, startAngle, endAngle);
        }

        internal static SvgPath GetSvgPath(this DxfEllipse ellipse)
        {
            return SvgPath.FromEllipse(ellipse.Center.X, ellipse.Center.Y, ellipse.MajorAxis.X, ellipse.MajorAxis.Y, ellipse.MinorAxisRatio, ellipse.StartParameter, ellipse.EndParameter);
        }

        internal static SvgPath GetSvgPath(this DxfLwPolyline poly)
        {
            var first = poly.Vertices.First();
            var segments = new List<SvgPathSegment>();
            segments.Add(new SvgMoveToPath(first.X, first.Y));
            var last = first;
            foreach (var next in poly.Vertices.Skip(1))
            {
                segments.Add(FromPolylineVertices(last, next));
                last = next;
            }

            if (poly.IsClosed)
            {
                segments.Add(FromPolylineVertices(last, first));
            }

            return new SvgPath(segments);
        }

        internal static SvgPath GetSvgPath(this DxfPolyline poly)
        {
            var first = poly.Vertices.First();
            var segments = new List<SvgPathSegment>();
            segments.Add(new SvgMoveToPath(first.Location.X, first.Location.Y));
            var last = first;
            foreach (var next in poly.Vertices.Skip(1))
            {
                segments.Add(FromPolylineVertices(last, next));
                last = next;
            }

            if (poly.IsClosed)
            {
                segments.Add(FromPolylineVertices(last, first));
            }

            return new SvgPath(segments);
        }

        internal static SvgPath GetSvgPath(this IList<Bezier2> beziers)
        {
            var first = beziers[0];
            var segments = new List<SvgPathSegment>();
            segments.Add(new SvgMoveToPath(first.Start.X, first.Start.Y));
            var last = first.Start;
            foreach (var next in beziers)
            {
                if (next.Start != last)
                {
                    segments.Add(new SvgMoveToPath(next.Start.X, next.Start.Y));
                }

                segments.Add(new SvgCubicBezierToPath(next.Control1.X, next.Control1.Y, next.Control2.X, next.Control2.Y, next.End.X, next.End.Y));
                last = next.End;
            }

            return new SvgPath(segments);
        }

        private static XElement AddStroke(this XElement element, DxfColor color)
        {
            if (color.IsIndex)
            {
                var colorString = color.ToRGBString();
                element.SetAttributeValue("stroke", colorString);
            }

            return element;
        }

        private static XElement AddStrokeWidth(this XElement element, double strokeWidth)
        {
            element.Add(new XAttribute("stroke-width", $"{Math.Max(strokeWidth, 1.0).ToDisplayString()}px"));
            return element;
        }

        private static XElement AddVectorEffect(this XElement element)
        {
            element.Add(new XAttribute("vector-effect", "non-scaling-stroke"));
            return element;
        }
    }
}
