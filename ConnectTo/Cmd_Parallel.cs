using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ConnectTo
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_Parallel : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                Reference referenceElementRef;
                try
                {
                    referenceElementRef = uiDoc.Selection.PickObject(
                        ObjectType.Element,
                        "Pick the reference element to define the parallel direction");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    TaskDialog.Show("Cancelled", "Operation cancelled before selecting reference element.");
                    return Result.Succeeded;
                }

                Element referenceElement = doc.GetElement(referenceElementRef);
                XYZ referenceDirection = GetElementDirection(referenceElement);
                XYZ projectedReferenceDirection = new XYZ(referenceDirection.X, referenceDirection.Y, 0).Normalize();

                List<RotationResult> rotationResults = new List<RotationResult>();

                bool continueSelection = true;
                int selectedCount = 0;
                while (continueSelection)
                {
                    IList<Reference> targetReferences = null;

                    try
                    {
                        //TaskDialogResult result = TaskDialog.Show(
                        //    "Selection",
                        //    "Select elements to rotate:\n\n- Click Yes for single selection\n- Click No for rectangle selection\n- Click Cancel to finish.",
                        //    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel);

                        //if (result == TaskDialogResult.Yes)
                        //{
                        //    Reference targetRef = uiDoc.Selection.PickObject(ObjectType.Element, "Pick a target element to rotate.");
                        //    targetReferences = new List<Reference> { targetRef };
                        //}
                        //else if (result == TaskDialogResult.No)
                        //{
                        //    targetReferences = uiDoc.Selection.PickObjects(ObjectType.Element, "Select elements to rotate.").ToList();
                        //}
                        //else
                        //{
                        //    continueSelection = false;
                        //    break;
                        //}
                        if (selectedCount == 0 || selectedCount > 1)
                        {
                            targetReferences = uiDoc.Selection.PickObjects(ObjectType.Element, "Select elements to rotate.").ToList();
                            selectedCount = targetReferences.Count;
                        }
                        else
                        {
                            Reference targetRef = uiDoc.Selection.PickObject(ObjectType.Element, "Pick a target element to rotate.");
                            targetReferences = new List<Reference> { targetRef };
                        }

                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        continueSelection = false;
                        break;
                    }

                    if (targetReferences == null || targetReferences.Count == 0)
                        continue;

                    using (Transaction trans = new Transaction(doc, "Make Elements Parallel"))
                    {
                        trans.Start();

                        foreach (var targetRef in targetReferences)
                        {
                            Element targetElement = doc.GetElement(targetRef);
                            XYZ targetDirection = GetElementDirection(targetElement);
                            XYZ targetOrigin = GetElementOrigin(targetElement);

                            if (targetDirection == null || targetOrigin == null)
                            {
                                TaskDialog.Show("Warning", $"Skipping element {targetElement.Id} because direction or origin could not be determined.");
                                continue;
                            }

                            XYZ projectedTargetDirection = new XYZ(targetDirection.X, targetDirection.Y, 0).Normalize();

                            double angle = projectedTargetDirection.AngleTo(projectedReferenceDirection);

                            if (angle > Math.PI / 2)
                                angle -= Math.PI;

                            XYZ normalVector = projectedTargetDirection.CrossProduct(projectedReferenceDirection);

                            if (XYZExtensions.IsZeroLength(normalVector))
                            {
                                rotationResults.Add(new RotationResult
                                {
                                    ElementId = targetElement.Id,
                                    RotationDegrees = 0.0
                                });
                                continue;
                            }

                            XYZ rotationAxisEndPoint = targetOrigin.Add(normalVector);
                            Line rotationAxis = Line.CreateBound(targetOrigin, rotationAxisEndPoint);

                            if (IsViewElement(targetElement))
                            {
                                ElevationMarker marker = GetElevationMarkerFromViewElement(targetElement, doc);
                                if (marker != null)
                                    targetElement = marker;
                            }

                            Location location = targetElement.Location;

                            if (location is LocationCurve locationCurve)
                            {
                                locationCurve.Rotate(rotationAxis, angle);
                            }
                            else if (location is LocationPoint locationPoint)
                            {
                                locationPoint.Rotate(rotationAxis, angle);
                            }
                            else
                            {
                                TaskDialog.Show("Error", $"Element {targetElement.Id} does not support rotation.");
                                continue;
                            }

                            rotationResults.Add(new RotationResult
                            {
                                ElementId = targetElement.Id,
                                RotationDegrees = angle * (180.0 / Math.PI)
                            });
                        }

                        trans.Commit();
                    }

                }

                if (rotationResults.Count > 0)
                {
                    //string report = string.Join(Environment.NewLine,
                    //    rotationResults.Select(r =>
                    //        $"Element Id: {r.ElementId.IntegerValue}, Rotation: {r.RotationDegrees:F2} degrees"));

                    //TaskDialog.Show("Parallel Rotation Summary", report);
                }
                else
                {
                    TaskDialog.Show("Parallel Rotation", "No elements were rotated.");
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An unexpected error occurred:\n{ex.Message}");
                return Result.Failed;
            }
        }

        private class RotationResult
        {
            public ElementId? ElementId { get; set; }
            public double RotationDegrees { get; set; }
        }


        #region Helper Methods
        private XYZ GetElementDirection(Element element)
        {
            // Try multiple methods in order logic
            List<Func<Element, XYZ>> directionMethods = new List<Func<Element, XYZ>>
            {
                GetGridDirection,
                GetReferencePlaneDirection,
                GetFamilyInstanceDirection,
                GetCurveElementDirection,
                GetSectionViewDirection
            };

            foreach (var getDirection in directionMethods)
            {
                try
                {
                    XYZ direction = getDirection(element);
                    if (direction != null)
                        return direction;
                }
                catch
                {
                    continue;
                }
            }

            TaskDialog.Show("Direction Error", $"Could not determine direction for element of type {element.GetType().Name} (Id: {element.Id.IntegerValue})");
            return XYZ.BasisX; // Default fallback to avoid nulls
        }

        private XYZ GetElementOrigin(Element element)
        {
            List<Func<Element, XYZ>> originMethods = new List<Func<Element, XYZ>>
            {
                GetGridOrigin,
                GetReferencePlaneOrigin,
                GetFamilyInstanceOrigin,
                GetCurveElementOrigin,
                GetSectionViewOrigin
            };

            foreach (var getOrigin in originMethods)
            {
                try
                {
                    XYZ origin = getOrigin(element);
                    if (origin != null)
                        return origin;
                }
                catch
                {
                    continue;
                }
            }

            TaskDialog.Show("Origin Error", $"Could not determine origin for element of type {element.GetType().Name} (Id: {element.Id.IntegerValue})");
            return XYZ.Zero; // Default fallback
        }

        private XYZ GetGridDirection(Element element)
        {
            if (element is Grid grid)
            {
                Curve curve = grid.Curve;
                XYZ direction = curve.GetEndPoint(1).Subtract(curve.GetEndPoint(0)).Normalize();
                return direction;
            }
            return null;
        }


        private XYZ GetGridOrigin(Element element)
        {
            if (element is Grid grid)
                return grid.Curve.GetEndPoint(0);
            return null;
        }

        private XYZ GetReferencePlaneDirection(Element element)
        {
            if (element is ReferencePlane referencePlane)
                return referencePlane.Direction;
            return null;
        }

        private XYZ GetReferencePlaneOrigin(Element element)
        {
            if (element is ReferencePlane referencePlane)
                return referencePlane.GetPlane().Origin;
            return null;
        }

        private XYZ GetFamilyInstanceDirection(Element element)
        {
            if (element is FamilyInstance familyInstance)
                return familyInstance.FacingOrientation;
            return null;
        }

        private XYZ GetFamilyInstanceOrigin(Element element)
        {
            if (element is FamilyInstance familyInstance)
                return familyInstance.GetTransform().Origin;
            return null;
        }

        private XYZ GetCurveElementDirection(Element element)
        {
            if (element.Location is LocationCurve locationCurve)
            {
                Curve curve = locationCurve.Curve;
                XYZ direction = curve.GetEndPoint(1).Subtract(curve.GetEndPoint(0)).Normalize();
                return direction;
            }
            return null;
        }


        private XYZ GetCurveElementOrigin(Element element)
        {
            if (element.Location is LocationCurve locationCurve)
                return locationCurve.Curve.GetEndPoint(0);
            return null;
        }

        private XYZ GetSectionViewDirection(Element element)
        {
            ViewSection view = GetViewFromElement(element);
            return view?.RightDirection;
        }

        private XYZ GetSectionViewOrigin(Element element)
        {
            ViewSection view = GetViewFromElement(element);
            return view?.Origin;
        }

        private ViewSection GetViewFromElement(Element element)
        {
            Parameter sketchPlaneParameter = element.get_Parameter(BuiltInParameter.VIEW_FIXED_SKETCH_PLANE);
            if (sketchPlaneParameter == null || !sketchPlaneParameter.HasValue)
                return null;

            ElementId sketchPlaneId = sketchPlaneParameter.AsElementId();
            SketchPlane sketchPlane = element.Document.GetElement(sketchPlaneId) as SketchPlane;
            if (sketchPlane == null)
                return null;

            ElementId ownerViewId = sketchPlane.OwnerViewId;
            return element.Document.GetElement(ownerViewId) as ViewSection;
        }

        private bool IsViewElement(Element element)
        {
            return element is ViewSection;
        }

        private ElevationMarker GetElevationMarkerFromViewElement(Element element, Document doc)
        {
            ViewSection view = element as ViewSection;
            if (view == null)
                return null;

            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(ElevationMarker));

            foreach (ElevationMarker marker in collector)
            {
                for (int i = 0; i < 4; i++)
                {
                    ElementId viewId = marker.GetViewId(i);
                    if (viewId == view.Id)
                    {
                        return marker;
                    }
                }
            }

            return null;
        }

        #endregion

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnParallel";
            string buttonTitle = "Parallel";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Blue_32,
                Properties.Resources.Blue_16,
                "This is a tooltip for Button 1");

            return myButtonData1.Data;
        }


    }
    public static class XYZExtensions
    {
        public static bool IsZeroLength(this XYZ vector)
        {
            return vector.GetLength() < 1e-8;
        }
    }

}
