using System;
using System.Reflection;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;


namespace ConnectTo
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_ConnectTo : IExternalCommand
    {
        // https://github.com/CyrilWaechter/pyRevitMEP/blob/master/pyRevitMEP.tab/Modify.panel/Connect.stack/ConnectTo.pushbutton/script.py
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                while (ConnectElements(uidoc, doc))
                {
                    // Repeat the process while user keeps connecting elements
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An unexpected error occurred: {ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private bool ConnectElements(UIDocument uidoc, Document doc)
        {
            Element movedElement = null;
            XYZ movedPoint = null;

            Element targetElement = null;
            XYZ targetPoint = null;

            try
            {
                Reference movedRef = uidoc.Selection.PickObject(
                    ObjectType.Element, new NoInsulationSelectionFilter(), "Pick element to move and connect");
                movedElement = doc.GetElement(movedRef.ElementId);
                movedPoint = movedRef.GlobalPoint;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }

            try
            {
                Reference targetRef = uidoc.Selection.PickObject(
                    ObjectType.Element, new NoInsulationSelectionFilter(), "Pick element to be connected to");
                targetElement = doc.GetElement(targetRef.ElementId);
                targetPoint = targetRef.GlobalPoint;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }

            if (targetElement.Id == movedElement.Id)
            {
                TaskDialog.Show("Error", "Oops, you selected the same object twice.");
                return true;
            }

            Connector movedConnector = GetClosestUnusedConnector(movedElement, movedPoint);
            Connector targetConnector = GetClosestUnusedConnector(targetElement, targetPoint);

            if (movedConnector == null || targetConnector == null)
            {
                TaskDialog.Show("Error", "One of the elements has no unused connector.");
                return true;
            }

            if (movedConnector.Domain != targetConnector.Domain)
            {
                TaskDialog.Show("Error", "You picked connectors from different domains.");
                return true;
            }

            XYZ movedDirection = movedConnector.CoordinateSystem.BasisZ;
            XYZ targetDirection = targetConnector.CoordinateSystem.BasisZ;

            using (Transaction tx = new Transaction(doc, "Connect elements"))
            {
                tx.Start();

                double angle = movedDirection.AngleTo(targetDirection);

                if (!IsAlmostEqual(angle, Math.PI))
                {
                    XYZ rotationAxis;
                    if (IsAlmostEqual(angle, 0))
                        rotationAxis = movedConnector.CoordinateSystem.BasisY;
                    else
                        rotationAxis = movedDirection.CrossProduct(targetDirection);

                    if (rotationAxis.IsZeroLength())
                        rotationAxis = XYZ.BasisZ;

                    try
                    {
                        Line axisLine = Line.CreateBound(movedPoint, movedPoint + rotationAxis);
                        Location location = movedElement.Location;
                        if (location is LocationCurve locCurve)
                        {
                            locCurve.Rotate(axisLine, angle - Math.PI);
                        }
                        else if (location is LocationPoint locPoint)
                        {
                            locPoint.Rotate(axisLine, angle - Math.PI);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentsInconsistentException)
                    {
                        TaskDialog.Show("Warning", "Rotation failed due to geometry constraints.");
                    }
                }

                XYZ moveVector = targetConnector.Origin - movedConnector.Origin;
                Location loc = movedElement.Location;
                if (loc is LocationCurve locCurve2)
                {
                    locCurve2.Move(moveVector);
                }
                else if (loc is LocationPoint locPoint2)
                {
                    locPoint2.Move(moveVector);
                }
                else
                {
                    TaskDialog.Show("Error", "Element location type unsupported for move.");
                    tx.RollBack();
                    return true;
                }

                movedConnector.ConnectTo(targetConnector);

                tx.Commit();
            }

            return true;
        }

        private Connector GetClosestUnusedConnector(Element element, XYZ referencePoint)
        {
            ConnectorSet connectors = GetUnusedConnectors(element);
            if (connectors == null || connectors.Size == 0)
                return null;

            Connector closest = null;
            double minDist = double.MaxValue;

            foreach (Connector conn in connectors)
            {
                double dist = conn.Origin.DistanceTo(referencePoint);
                if (dist < minDist)
                {
                    closest = conn;
                    minDist = dist;
                }
            }

            return closest;
        }

        private ConnectorSet GetUnusedConnectors(Element element)
        {
            // This replicates pyRevit's get_connector_manager()
            if (element is FamilyInstance famInst)
            {
                var mepModel = famInst.MEPModel;
                if (mepModel != null)
                    return mepModel.ConnectorManager?.UnusedConnectors;
            }
            else
            {
                var prop = element.GetType().GetProperty("ConnectorManager");
                if (prop != null)
                {
                    var manager = prop.GetValue(element, null);
                    var unusedProp = manager?.GetType().GetProperty("UnusedConnectors");
                    if (unusedProp != null)
                        return unusedProp.GetValue(manager, null) as ConnectorSet;
                }
            }

            return null;
        }

        private bool IsAlmostEqual(double a, double b, double tolerance = 1e-6)
        {
            return Math.Abs(a - b) < tolerance;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnCommand1";
            string buttonTitle = "ConnectTo";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.ConnectTo_32x32,
                Properties.Resources.ConnectTo_16x16,
                "This button will connect air ducts or pipes. The first object end selected will move and connect to the second object you select. The command remains active to allow you to continue connecting elements until you Esc out of the command.");

            return myButtonData1.Data;
        }
    }

    public class NoInsulationSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is InsulationLiningBase)
                return false;

            // This replicates pyRevit's logic
            if (elem is FamilyInstance famInst)
            {
                var mepModel = famInst.MEPModel;
                if (mepModel != null && mepModel.ConnectorManager != null)
                    return true;
            }
            else
            {
                var prop = elem.GetType().GetProperty("ConnectorManager");
                if (prop != null)
                    return true;
            }

            return false;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return true;
        }
    }
}

