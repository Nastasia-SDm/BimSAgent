using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace BimS.Revit2024
{
    internal static class ViewGraphReader
    {
        private static object Id(ElementId id) => id != null && id.Value > 0 ? (object)id.Value : null;
        private static object Point(XYZ p) => p == null ? null : (object)new { x = p.X, y = p.Y, z = p.Z };
        private static object UVPoint(UV p) => new { u = p.U, v = p.V };
        private static object CurveData(Curve curve)
        {
            if (curve == null) return null;
            return new { kind = curve.GetType().Name, isBound = curve.IsBound,
                start = curve.IsBound ? Point(curve.GetEndPoint(0)) : null,
                end = curve.IsBound ? Point(curve.GetEndPoint(1)) : null,
                sampledPoints = curve.IsBound ? curve.Tessellate().Select(Point).ToArray() : null };
        }
        private static object Box(BoundingBoxXYZ box) => box == null ? null : (object)new
        {
            min = Point(box.Min), max = Point(box.Max), transform = new
            { origin = Point(box.Transform.Origin), basisX = Point(box.Transform.BasisX), basisY = Point(box.Transform.BasisY), basisZ = Point(box.Transform.BasisZ) }
        };
        private static object OutlineData(Outline o) => o == null ? null : (object)new { min = Point(o.MinimumPoint), max = Point(o.MaximumPoint) };
        private sealed class Row
        {
            internal readonly Dictionary<string, object> Data = new Dictionary<string, object>();
            internal readonly Dictionary<string, object> Properties = new Dictionary<string, object>();
            private readonly List<object> errors = new List<object>();
            internal Row(Element e, string kind)
            {
                Data["elementId"] = e.Id.Value; Data["kind"] = kind; Data["class"] = e.GetType().FullName;
                Data["status"] = "ok"; Data["errors"] = errors; Data["properties"] = Properties;
                ReadCommon("uniqueId", () => e.UniqueId); ReadCommon("name", () => e.Name);
                ReadCommon("categoryId", () => e.Category == null ? null : (object)e.Category.Id.Value);
                ReadCommon("category", () => e.Category?.Name);
                ReadCommon("ownerViewId", () => Id(e.OwnerViewId)); ReadCommon("typeId", () => Id(e.GetTypeId()));
                ReadCommon("typeName", () => e.Document.GetElement(e.GetTypeId())?.Name);
                ReadCommon("familyName", () => (e.Document.GetElement(e.GetTypeId()) as ElementType)?.FamilyName);
            }
            private void Error(string field, Exception ex)
            {
                Data["status"] = "partial";
                errors.Add(new { field, message = ex.GetType().Name });
            }
            private void ReadCommon(string field, Func<object> get)
            {
                try { Data[field] = get(); }
                catch (Exception ex) { Data[field] = null; Error(field, ex); }
            }
            internal void Read(string key, Func<object> get, string source, string unit = null, string coordinates = null)
            {
                try
                {
                    var value = get();
                    Properties[key] = new { status = value == null ? "noValue" : "ok", value, source, unit, coordinateSystem = coordinates };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Properties[key] = new { status = "error", value = (object)null, source, unit, coordinateSystem = coordinates };
                    Error(key, ex);
                }
            }
            internal void Unavailable(string key, string status, string source)
            { Properties[key] = new { status, value = (object)null, source }; }
        }

        internal static object Read(Document document, string session, ViewGraphRequest request, Action check)
        {
            var allSheets = new Dictionary<long, ViewSheet>(); var allViews = new Dictionary<long, View>();
            var allPlacements = new List<Element>(); var annotations = new List<Element>();
            using (var collector = new FilteredElementCollector(document).WhereElementIsNotElementType())
                foreach (var e in collector)
                {
                    check();
                    if (e is ViewSheet sheet) allSheets[sheet.Id.Value] = sheet;
                    else if (e is View view && (request.IncludeTemplates || !view.IsTemplate) && view.ViewType != ViewType.ProjectBrowser && view.ViewType != ViewType.SystemBrowser)
                        allViews[view.Id.Value] = view;
                    else if (e is Viewport || e is ScheduleSheetInstance) allPlacements.Add(e);
                    else if (AnnotationKind(e) is string kind && (request.IncludeAnnotations || kind == "titleBlock")) annotations.Add(e);
                }
            if (request.SheetIds.Any(id => !allSheets.ContainsKey(id)) || request.ViewIds.Any(id => !allViews.ContainsKey(id)))
                throw new ArgumentException("Запрошенные листы/виды не найдены или исключены фильтром шаблонов.");
            Func<Element, long> sheetId = e => e is Viewport vp ? vp.SheetId.Value : e.OwnerViewId.Value;
            Func<Element, long> viewId = e => e is Viewport vp ? vp.ViewId.Value : ((ScheduleSheetInstance)e).ScheduleId.Value;
            var selectedSheets = new HashSet<long>(request.Scope == "document" ? (IEnumerable<long>)allSheets.Keys : request.SheetIds);
            var selectedViews = new HashSet<long>(request.Scope == "document" ? (IEnumerable<long>)allViews.Keys : request.ViewIds);
            var placements = allPlacements.Where(e => allSheets.ContainsKey(sheetId(e)) && allViews.ContainsKey(viewId(e)) &&
                (request.Scope == "document" || request.Scope == "sheets" && selectedSheets.Contains(sheetId(e)) || request.Scope == "views" && selectedViews.Contains(viewId(e))))
                .OrderBy(e => e.Id.Value).ToArray();
            foreach (var p in placements) { selectedSheets.Add(sheetId(p)); selectedViews.Add(viewId(p)); }
            var rows = new Dictionary<string, List<Dictionary<string, object>>>
            {
                ["sheets"] = new List<Dictionary<string, object>>(), ["views"] = new List<Dictionary<string, object>>(),
                ["placements"] = new List<Dictionary<string, object>>(), ["elements"] = new List<Dictionary<string, object>>()
            };
            var relations = new List<object>();
            foreach (var id in selectedSheets.OrderBy(id => id))
            {
                check(); var sheet = allSheets[id]; var row = new Row(sheet, "sheet");
                row.Read("number", () => sheet.SheetNumber, "ViewSheet.SheetNumber");
                row.Read("isPlaceholder", () => sheet.IsPlaceholder, "ViewSheet.IsPlaceholder");
                row.Read("outline", () => new { min = UVPoint(sheet.Outline.Min), max = UVPoint(sheet.Outline.Max) }, "View.Outline", "ft", "sheet");
                rows["sheets"].Add(row.Data);
            }
            foreach (var id in selectedViews.OrderBy(id => id))
            {
                check(); var view = allViews[id]; var row = new Row(view, "view");
                row.Read("viewType", () => view.ViewType.ToString(), "View.ViewType");
                row.Read("scale", () => view.Scale, "View.Scale");
                row.Read("isTemplate", () => view.IsTemplate, "View.IsTemplate");
                row.Read("templateId", () => Id(view.ViewTemplateId), "View.ViewTemplateId");
                row.Read("primaryViewId", () => Id(view.GetPrimaryViewId()), "View.GetPrimaryViewId");
                row.Read("dependentViewIds", () => view.GetDependentViewIds().Select(Id).ToArray(), "View.GetDependentViewIds");
                if (view is ViewSchedule || view.IsTemplate || view.ViewType == ViewType.Legend || view.ViewType == ViewType.DraftingView)
                {
                    row.Unavailable("cropActive", "notApplicable", "View.CropBoxActive"); row.Unavailable("cropBox", "notApplicable", "View.CropBox");
                }
                else
                {
                    row.Read("cropActive", () => view.CropBoxActive, "View.CropBoxActive");
                    row.Read("cropBox", () => Box(view.CropBox), "View.CropBox (local bounds + model transform)", "ft", "view-local-to-model");
                }
                rows["views"].Add(row.Data);
            }
            foreach (var p in placements)
            {
                check(); var row = new Row(p, p is Viewport ? "viewport" : "schedulePlacement");
                relations.Add(new { kind = "onSheet", fromId = p.Id.Value, toId = sheetId(p) });
                relations.Add(new { kind = "placedView", fromId = p.Id.Value, toId = viewId(p) });
                row.Read("sheetId", () => sheetId(p), "placement sheet"); row.Read("viewId", () => viewId(p), "placement view");
                if (p is Viewport vp)
                {
                    row.Read("center", () => Point(vp.GetBoxCenter()), "Viewport.GetBoxCenter", "ft", "sheet");
                    row.Read("outline", () => OutlineData(vp.GetBoxOutline()), "Viewport.GetBoxOutline", "ft", "sheet");
                    row.Read("rotation", () => vp.Rotation.ToString(), "Viewport.Rotation");
                    row.Read("detailNumber", () => vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString(), "VIEWPORT_DETAIL_NUMBER");
                    row.Read("labelOffset", () => Point(vp.LabelOffset), "Viewport.LabelOffset", "ft", "viewport-local");
                    row.Read("labelLineLength", () => vp.LabelLineLength, "Viewport.LabelLineLength", "ft");
                }
                else if (p is ScheduleSheetInstance schedule)
                {
                    row.Read("point", () => Point(schedule.Point), "ScheduleSheetInstance.Point", "ft", "sheet");
                    row.Read("rotation", () => schedule.Rotation, "ScheduleSheetInstance.Rotation", "rad");
                    row.Read("segmentIndex", () => schedule.SegmentIndex, "ScheduleSheetInstance.SegmentIndex");
                    row.Read("isTitleblockRevisionSchedule", () => schedule.IsTitleblockRevisionSchedule, "ScheduleSheetInstance.IsTitleblockRevisionSchedule");
                    row.Read("box", () => Box(schedule.get_BoundingBox(allSheets[sheetId(p)])), "Element.get_BoundingBox", "ft", "sheet");
                }
                rows["placements"].Add(row.Data);
            }
            // View scope includes annotations belonging to selected views, not unrelated annotations on context sheets.
            var owners = new HashSet<long>(selectedViews);
            if (request.Scope != "views") owners.UnionWith(selectedSheets);
            foreach (var e in annotations.OrderBy(e => e.Id.Value))
            {
                check(); if (!owners.Contains(e.OwnerViewId.Value)) continue;
                var row = ReadAnnotation(e, document, check);
                rows["elements"].Add(row.Data);
                relations.Add(new { kind = "ownedBy", fromId = e.Id.Value, toId = e.OwnerViewId.Value });
            }
            var entityIds = new HashSet<long>(rows.Values.SelectMany(v => v).Select(r => (long)r["elementId"]));
            foreach (var group in annotations.OfType<Group>().Where(g => entityIds.Contains(g.Id.Value)))
                foreach (var id in group.GetMemberIds().Where(id => entityIds.Contains(id.Value)))
                    relations.Add(new { kind = "groupMember", fromId = group.Id.Value, toId = id.Value });
            var result = new Dictionary<string, object>
            {
                ["contractVersion"] = 1, ["documentSession"] = session,
                ["document"] = new { title = document.Title, revitVersion = document.Application.VersionNumber },
                ["status"] = rows.Values.SelectMany(v => v).Any(r => (string)r["status"] != "ok") ? "partial" : "complete",
                ["errors"] = new object[0], ["warnings"] = new[] {
                    "Отбор аннотаций по OwnerViewId; видимость, crop и скрытие на печати не вычисляются.",
                    "Снимок отражает интервал чтений; documentSession не является ревизией документа.",
                    "Параметры: только BuiltInParameter. Неподдерживаемые специализированные свойства помечены явно." }
            };
            foreach (var field in request.Fields)
                result[char.ToLowerInvariant(field[0]) + field.Substring(1)] = field == "Relations" ? (object)relations : rows[char.ToLowerInvariant(field[0]) + field.Substring(1)];
            return result;
        }

        private static string AnnotationKind(Element e)
        {
            if (e is Dimension) return "dimension";
            if (e is IndependentTag || e is SpatialElementTag) return "tag";
            if (e is TextNote) return "text";
            if (e is DetailCurve) return "detailCurve";
            if (e is FilledRegion) return "region";
            if (e is RevisionCloud) return "revisionCloud";
            var category = e.Category == null ? BuiltInCategory.INVALID : (BuiltInCategory)e.Category.Id.Value;
            if (e is Group && category == BuiltInCategory.OST_IOSDetailGroups) return "detailGroup";
            if (e is FamilyInstance)
            {
                if (category == BuiltInCategory.OST_TitleBlocks) return "titleBlock";
                if (category == BuiltInCategory.OST_DetailComponents) return "detailComponent";
                if (category == BuiltInCategory.OST_GenericAnnotation) return "genericAnnotation";
            }
            return null;
        }
        private static Row ReadAnnotation(Element e, Document document, Action check)
        {
            var row = new Row(e, AnnotationKind(e));
            var coordinates = document.GetElement(e.OwnerViewId) is ViewSheet ? "sheet" : "model";
            row.Read("box", () => Box(e.get_BoundingBox(document.GetElement(e.OwnerViewId) as View)), "Element.get_BoundingBox", "ft", coordinates);
            if (e is Dimension dimension)
            {
                row.Read("shape", () => dimension.DimensionShape.ToString(), "Dimension.DimensionShape");
                row.Read("styleType", () => dimension.DimensionType.StyleType.ToString(), "DimensionType.StyleType");
                row.Read("referencesAvailable", () => dimension.AreReferencesAvailable, "Dimension.AreReferencesAvailable");
                row.Read("references", () => dimension.References.Cast<Reference>().Select(r => ReferenceData(document, r)).ToArray(), "Dimension.References");
                if (dimension.NumberOfSegments == 0)
                {
                    row.Read("value", () => dimension.Value, "Dimension.Value", "Revit internal; angle=rad, length=ft (see styleType)");
                    row.Read("displayValue", () => dimension.ValueString, "Dimension.ValueString");
                    row.Read("text", () => new { valueOverride = dimension.ValueOverride, prefix = dimension.Prefix, suffix = dimension.Suffix, above = dimension.Above, below = dimension.Below }, "Dimension text");
                    if (dimension.IsTextPositionAdjustable()) row.Read("textPosition", () => Point(dimension.TextPosition), "Dimension.TextPosition", "ft", coordinates);
                    else row.Unavailable("textPosition", "notApplicable", "Dimension.IsTextPositionAdjustable");
                }
                else
                {
                    row.Unavailable("value", "notApplicable", "See segments");
                    row.Read("segments", () => dimension.Segments.Cast<DimensionSegment>().Select((s, index) =>
                    {
                        check(); return new { index, value = s.Value, displayValue = s.ValueString, valueOverride = s.ValueOverride,
                            prefix = s.Prefix, suffix = s.Suffix, above = s.Above, below = s.Below,
                            textPosition = s.IsTextPositionAdjustable() ? Point(s.TextPosition) : null };
                    }).ToArray(), "Dimension.Segments (index is not ElementId)", "Revit internal; angle=rad, length=ft", coordinates);
                }
                row.Read("curve", () => CurveData(dimension.Curve), "Dimension.Curve", "ft", coordinates);
            }
            else if (e is IndependentTag tag)
            {
                row.Read("text", () => tag.TagText, "IndependentTag.TagText");
                row.Read("head", () => Point(tag.TagHeadPosition), "IndependentTag.TagHeadPosition", "ft", coordinates);
                row.Read("orphaned", () => tag.IsOrphaned, "IndependentTag.IsOrphaned");
                row.Read("targets", () => tag.GetTaggedElementIds().Select(t => LinkData(document, t)).ToArray(), "IndependentTag.GetTaggedElementIds");
                row.Read("hasLeader", () => tag.HasLeader, "IndependentTag.HasLeader");
                row.Read("leaders", () => !tag.HasLeader ? new object[0] : tag.GetTaggedReferences().Select(r => (object)new
                {
                    reference = ReferenceData(document, r),
                    elbow = tag.HasLeaderElbow(r) ? Point(tag.GetLeaderElbow(r)) : null,
                    end = tag.LeaderEndCondition == LeaderEndCondition.Free ? Point(tag.GetLeaderEnd(r)) : null,
                    endStatus = tag.LeaderEndCondition == LeaderEndCondition.Free ? "ok" : "notApplicable"
                }).ToArray(), "IndependentTag leaders", "ft", coordinates);
            }
            else if (e is SpatialElementTag spatial)
            {
                row.Read("text", () => spatial.TagText, "SpatialElementTag.TagText");
                row.Read("head", () => Point(spatial.TagHeadPosition), "SpatialElementTag.TagHeadPosition", "ft", coordinates);
                row.Read("orphaned", () => spatial.IsOrphaned, "SpatialElementTag.IsOrphaned");
                row.Read("isTaggingLink", () => spatial.IsTaggingLink, "SpatialElementTag.IsTaggingLink");
                if (spatial is Autodesk.Revit.DB.Architecture.RoomTag room)
                    row.Read("targets", () => new[] { LinkData(document, room.TaggedRoomId) }, "RoomTag.TaggedRoomId");
                else if (spatial is Autodesk.Revit.DB.AreaTag area && !spatial.IsTaggingLink)
                    row.Read("targetId", () => Id(area.Area?.Id), "AreaTag.Area");
                else if (spatial is Autodesk.Revit.DB.Mechanical.SpaceTag space && !spatial.IsTaggingLink)
                    row.Read("targetId", () => Id(space.Space?.Id), "SpaceTag.Space");
                else row.Unavailable("targets", "unsupported", "Linked non-room spatial tag: no validated link reader");
                row.Read("leaders", () => new { hasLeader = spatial.HasLeader,
                    elbow = spatial.HasElbow ? Point(spatial.LeaderElbow) : null,
                    end = spatial.HasLeader ? Point(spatial.LeaderEnd) : null }, "SpatialElementTag leaders", "ft", coordinates);
            }
            else if (e is TextNote text)
            {
                row.Read("text", () => text.Text, "TextElement.Text");
                row.Read("position", () => Point(text.Coord), "TextElement.Coord", "ft", coordinates);
                row.Read("width", () => text.Width, "TextElement.Width", "ft", "paper");
                row.Read("alignment", () => new { horizontal = text.HorizontalAlignment.ToString(), vertical = text.VerticalAlignment.ToString() }, "TextElement alignment");
                row.Read("orientation", () => new { baseDirection = Point(text.BaseDirection), upDirection = Point(text.UpDirection) }, "TextElement directions", "unit vector", coordinates);
                row.Read("rotation", () =>
                {
                    var owner = (View)document.GetElement(text.OwnerViewId);
                    return Math.Atan2(text.BaseDirection.DotProduct(owner.UpDirection), text.BaseDirection.DotProduct(owner.RightDirection));
                }, "TextElement.BaseDirection projected onto owner View.RightDirection/UpDirection", "rad", "view-plane");
                row.Read("leaders", () => text.GetLeaders().Select(l => new { anchor = Point(l.Anchor), elbow = Point(l.Elbow), end = Point(l.End), shape = l.LeaderShape.ToString() }).ToArray(), "TextNote.GetLeaders", "ft", coordinates);
                row.Read("formatting", () =>
                {
                    using (var formatted = text.GetFormattedText())
                        return new { bold = formatted.GetBoldStatus().ToString(), italic = formatted.GetItalicStatus().ToString(),
                            underline = formatted.GetUnderlineStatus().ToString(), allCaps = formatted.GetAllCapsStatus().ToString(),
                            superscript = formatted.GetSuperscriptStatus().ToString(), subscript = formatted.GetSubscriptStatus().ToString() };
                }, "TextNote.GetFormattedText; whole text status (may be Mixed)");
                row.Unavailable("formattedRuns", "unsupported", "Per-range rich text formatting is not exported in v1");
            }
            else if (e is DetailCurve curve)
            {
                row.Read("curve", () => CurveData(curve.GeometryCurve), "CurveElement.GeometryCurve", "ft", coordinates);
                row.Read("lineStyle", () => new { id = Id(curve.LineStyle?.Id), name = curve.LineStyle?.Name }, "CurveElement.LineStyle");
            }
            else if (e is FilledRegion region)
            {
                row.Read("isMasking", () => region.IsMasking, "FilledRegion.IsMasking");
                row.Read("boundaries", () => region.GetBoundaries().Select(loop => loop.Select(CurveData).ToArray()).ToArray(), "FilledRegion.GetBoundaries", "ft", coordinates);
            }
            else if (e is RevisionCloud cloud)
            {
                row.Read("revisionId", () => Id(cloud.RevisionId), "RevisionCloud.RevisionId");
                row.Read("curves", () => cloud.GetSketchCurves().Select(CurveData).ToArray(), "RevisionCloud.GetSketchCurves", "ft", coordinates);
            }
            else if (e is Group group)
                row.Read("memberIds", () => group.GetMemberIds().Select(Id).ToArray(), "Group.GetMemberIds");
            else if (e is FamilyInstance instance)
            {
                row.Read("position", () => Point((instance.Location as LocationPoint)?.Point), "LocationPoint.Point", "ft", coordinates);
                row.Read("rotation", () => (instance.Location as LocationPoint)?.Rotation, "LocationPoint.Rotation", "rad", coordinates);
            }
            return row;
        }
        private static object ReferenceData(Document document, Reference reference)
        {
            // Unresolved references are retained with their own status, rather than disappearing.
            try
            {
                var host = document.GetElement(reference.ElementId);
                var link = host as RevitLinkInstance;
                var linked = link?.GetLinkDocument();
                return new { status = host == null || (Id(reference.LinkedElementId) != null && linked?.GetElement(reference.LinkedElementId) == null) ? "unresolved" : "ok",
                    elementId = Id(reference.ElementId), linkedElementId = Id(reference.LinkedElementId),
                    linkedDocument = linked?.Title, stableRepresentation = reference.ConvertToStableRepresentation(document) };
            }
            catch (Exception ex) { return new { status = "unresolved", elementId = Id(reference.ElementId),
                linkedElementId = Id(reference.LinkedElementId), error = ex.GetType().Name }; }
        }
        private static object LinkData(Document document, LinkElementId target)
        {
            var link = document.GetElement(target.LinkInstanceId) as RevitLinkInstance;
            var linked = link?.GetLinkDocument();
            var isLink = Id(target.LinkInstanceId) != null;
            var resolved = isLink ? linked?.GetElement(target.LinkedElementId) != null : document.GetElement(target.HostElementId) != null;
            return new { status = resolved ? "ok" : "unresolved", hostElementId = Id(target.HostElementId),
                linkInstanceId = Id(target.LinkInstanceId), linkedElementId = Id(target.LinkedElementId), linkedDocument = linked?.Title };
        }
    }
}
