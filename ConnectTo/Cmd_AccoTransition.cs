using System;
using System.Collections.Generic;
using System.Linq;
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
    public class Cmd_AccoTransition : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationNoFamilyInstanceFilter(),
                    "Pick element 1 (movable connector)");
                Element elem1 = doc.GetElement(pick1);
                XYZ xyz1 = pick1.GlobalPoint;

                Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationFilter(),
                    "Pick element 2 (static connector)");
                Element elem2 = doc.GetElement(pick2);
                XYZ xyz2 = pick2.GlobalPoint;

                Connector conn1 = ConnectorUtils.GetClosestUnusedConnector(elem1, xyz1);
                Connector conn2 = ConnectorUtils.GetClosestUnusedConnector(elem2, xyz2);

                if (conn1 == null || conn2 == null)
                {
                    TaskDialog.Show("Error", "One of the selected elements has no unused connector.");
                    return Result.Cancelled;
                }

                if (conn1.Domain != conn2.Domain)
                {
                    TaskDialog.Show("Domain Error", "Connectors are from different domains.");
                    return Result.Cancelled;
                }

                FamilyInstance fittingInstance = null;

                using (Transaction tx = new Transaction(doc, "Create ACCO Transition"))
                {
                    tx.Start();
                    fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
                    tx.Commit();
                }

                if (fittingInstance == null)
                {
                    TaskDialog.Show("Error", "Failed to create transition fitting.");
                    return Result.Failed;
                }

                if (FittingEvaluator.TryFindBestFitting(doc, fittingInstance, out FamilySymbol bestSymbol))
                {
                    using (Transaction tx = new Transaction(doc, "Assign Best Fitting Type"))
                    {
                        tx.Start();
                        fittingInstance.Symbol = bestSymbol;
                        tx.Commit();
                    }
                }
                else
                {
                    TaskDialog td = new TaskDialog("Angle Constraint")
                    {
                        MainInstruction = "All transition types exceed 30° angle limit.",
                        MainContent = "Do you want to keep the default fitting anyway?",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };

                    if (td.Show() == TaskDialogResult.No)
                    {
                        using (Transaction tx = new Transaction(doc, "Delete Fitting"))
                        {
                            tx.Start();
                            doc.Delete(fittingInstance.Id);
                            tx.Commit();
                        }
                        return Result.Cancelled;
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnAccoTransition";
            string buttonTitle = "ACCO Transition";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Blue_32,
                Properties.Resources.Blue_16,
                "This button creates an MEP transition fitting between two open unused connectors if the ACCO Angle Constrains are met."
            );

            return myButtonData1.Data;
        }
    }

    public static class FittingEvaluator
    {
        public static bool TryFindBestFitting(Document doc, FamilyInstance fittingInstance, out FamilySymbol? bestSymbol)
        {
            bestSymbol = null;

            Family family = fittingInstance.Symbol.Family;
            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Family.Id == family.Id)
                .OrderBy(s => TransitionSettings.LengthsInches.IndexOf(GetLengthFromName(s.Name)));

            foreach (FamilySymbol symbol in symbols)
            {
                using (Transaction tx = new Transaction(doc, "Switch Transition Type"))
                {
                    tx.Start();
                    fittingInstance.Symbol = symbol;
                    tx.Commit();
                }

                double angleTop = GetAngleParameterValue(fittingInstance, "Angle Top");
                double angleBottom = GetAngleParameterValue(fittingInstance, "Angle Bottom");
                double angleLeft = GetAngleParameterValue(fittingInstance, "Angle Left");
                double angleRight = GetAngleParameterValue(fittingInstance, "Angle Right");

                if (Math.Abs(angleTop) <= TransitionSettings.MaxAngleDeg &&
                    Math.Abs(angleBottom) <= TransitionSettings.MaxAngleDeg &&
                    Math.Abs(angleLeft) <= TransitionSettings.MaxAngleDeg &&
                    Math.Abs(angleRight) <= TransitionSettings.MaxAngleDeg)
                {
                    bestSymbol = symbol;
                    return true;
                }
            }

            return false;
        }

        private static int GetLengthFromName(string name)
        {
            foreach (int len in TransitionSettings.LengthsInches)
            {
                if (name.Contains($"{len}"))
                    return len;
            }
            return int.MaxValue;
        }

        private static double GetAngleParameterValue(FamilyInstance fi, string paramName)
        {
            Parameter param = fi.LookupParameter(paramName);
            if (param != null && param.HasValue)
            {
                return param.AsDouble() * (180.0 / Math.PI);
            }
            return double.MaxValue;
        }
    }

    public static class TransitionSettings
    {
        public static double MaxAngleDeg = 30.0;
        public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };
    }

    public static class ConnectorUtils
    {
        public static Connector GetClosestUnusedConnector(Element element, XYZ point)
        {
            ConnectorSet connectors = GetConnectorManager(element)?.UnusedConnectors;
            if (connectors == null || connectors.IsEmpty) return null;

            Connector closest = null;
            double minDist = double.MaxValue;

            foreach (Connector c in connectors)
            {
                double dist = c.Origin.DistanceTo(point);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = c;
                }
            }
            return closest;
        }

        public static ConnectorManager GetConnectorManager(Element element)
        {
            if (element is MEPCurve mepCurve)
                return mepCurve.ConnectorManager;
            if (element is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;
            return null;
        }
    }

    // This class is used to allow the selections of DuctTypes only.
    public static class SelectionFilters
    {
        public class NoInsulationFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is not Duct) return false;
                if (elem is InsulationLiningBase) return false;
                return ConnectorUtils.GetConnectorManager(elem) != null;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is not Duct) return false;
                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
                return ConnectorUtils.GetConnectorManager(elem) != null;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
    //public static class SelectionFilters1
    //{
    //    public class NoInsulationFilter : ISelectionFilter
    //    {
    //        public bool AllowElement(Element elem)
    //        {
    //            if (elem is InsulationLiningBase) return false;
    //            return ConnectorUtils.GetConnectorManager(elem) != null;
    //        }
    //        public bool AllowReference(Reference reference, XYZ position) => true;
    //    }

    //    public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
    //    {
    //        public bool AllowElement(Element elem)
    //        {
    //            if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
    //            return ConnectorUtils.GetConnectorManager(elem) != null;
    //        }
    //        public bool AllowReference(Reference reference, XYZ position) => true;
    //    }
    //}
}



//##################################################################################
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Reflection;

//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.DB.Mechanical;
//using Autodesk.Revit.DB.Plumbing;
//using Autodesk.Revit.UI;
//using Autodesk.Revit.UI.Selection;

//namespace ConnectTo
//{
//    [Transaction(TransactionMode.Manual)]
//    public class Cmd_AccoTransition : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIApplication uiapp = commandData.Application;
//            UIDocument uidoc = uiapp.ActiveUIDocument;
//            Document doc = uidoc.Document;

//            try
//            {
//                Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationNoFamilyInstanceFilter(),
//                    "Pick element 1 (movable connector)");
//                Element elem1 = doc.GetElement(pick1);
//                XYZ xyz1 = pick1.GlobalPoint;

//                Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationFilter(),
//                    "Pick element 2 (static connector)");
//                Element elem2 = doc.GetElement(pick2);
//                XYZ xyz2 = pick2.GlobalPoint;

//                Connector conn1 = ConnectorUtils.GetClosestUnusedConnector(elem1, xyz1);
//                Connector conn2 = ConnectorUtils.GetClosestUnusedConnector(elem2, xyz2);

//                if (conn1 == null || conn2 == null)
//                {
//                    TaskDialog.Show("Error", "One of the selected elements has no unused connector.");
//                    return Result.Cancelled;
//                }

//                if (conn1.Domain != conn2.Domain)
//                {
//                    TaskDialog.Show("Domain Error", "Connectors are from different domains.");
//                    return Result.Cancelled;
//                }

//                FamilyInstance fittingInstance = null;

//                using (Transaction tx = new Transaction(doc, "Create ACCO Transition"))
//                {
//                    tx.Start();
//                    fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
//                    tx.Commit();
//                }

//                if (fittingInstance == null)
//                {
//                    TaskDialog.Show("Error", "Failed to create transition fitting.");
//                    return Result.Failed;
//                }

//                Family family = fittingInstance.Symbol.Family;
//                IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc)
//                    .OfClass(typeof(FamilySymbol))
//                    .Cast<FamilySymbol>()
//                    .Where(s => s.Family.Id == family.Id)
//                    .OrderBy(s => TransitionSettings.LengthsInches.IndexOf(GetLengthFromName(s.Name)));

//                foreach (FamilySymbol symbol in symbols)
//                {
//                    using (Transaction tx = new Transaction(doc, "Switch Transition Type"))
//                    {
//                        tx.Start();
//                        fittingInstance.Symbol = symbol;
//                        tx.Commit();
//                    }

//                    double angleTop = GetAngleParameterValue(fittingInstance, "Angle Top");
//                    double angleBottom = GetAngleParameterValue(fittingInstance, "Angle Bottom");
//                    double angleLeft = GetAngleParameterValue(fittingInstance, "Angle Left");
//                    double angleRight = GetAngleParameterValue(fittingInstance, "Angle Right");

//                    if (Math.Abs(angleTop) <= TransitionSettings.MaxAngleDeg &&
//                        Math.Abs(angleBottom) <= TransitionSettings.MaxAngleDeg &&
//                        Math.Abs(angleLeft) <= TransitionSettings.MaxAngleDeg &&
//                        Math.Abs(angleRight) <= TransitionSettings.MaxAngleDeg)
//                    {
//                        return Result.Succeeded; // Found acceptable type
//                    }
//                }

//                TaskDialog td = new TaskDialog("Angle Constraint")
//                {
//                    MainInstruction = "All transition types exceed 30° angle limit.",
//                    MainContent = "Do you want to keep the default fitting anyway?",
//                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                    DefaultButton = TaskDialogResult.No
//                };

//                if (td.Show() == TaskDialogResult.No)
//                {
//                    using (Transaction tx = new Transaction(doc, "Delete Fitting"))
//                    {
//                        tx.Start();
//                        doc.Delete(fittingInstance.Id);
//                        tx.Commit();
//                    }
//                    return Result.Cancelled;
//                }
//            }
//            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
//            {
//                return Result.Cancelled;
//            }
//            catch (Exception ex)
//            {
//                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
//                return Result.Failed;
//            }

//            return Result.Succeeded;
//        }

//        private static int GetLengthFromName(string name)
//        {
//            foreach (int len in TransitionSettings.LengthsInches)
//            {
//                if (name.Contains($"{len}"))
//                    return len;
//            }
//            return int.MaxValue;
//        }

//        private static double GetAngleParameterValue(FamilyInstance fi, string paramName)
//        {
//            Parameter param = fi.LookupParameter(paramName);
//            if (param != null && param.HasValue)
//            {
//                return param.AsDouble() * (180.0 / Math.PI);
//            }
//            return double.MaxValue;
//        }

//        internal static PushButtonData GetButtonData()
//        {
//            string buttonInternalName = "btnAccoTransition";
//            string buttonTitle = "ACCO Transition";

//            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
//                buttonInternalName,
//                buttonTitle,
//                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
//                Properties.Resources.Blue_32,
//                Properties.Resources.Blue_16,
//                "This button creates an MEP transition fitting between two open unused connectors if the ACCO Angle Constrains are met."
//            );

//            return myButtonData1.Data;
//        }
//    }

//    public static class TransitionSettings
//    {
//        public static double MaxAngleDeg = 30.0;
//        public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };
//    }

//    public static class ConnectorUtils
//    {
//        public static Connector GetClosestUnusedConnector(Element element, XYZ point)
//        {
//            ConnectorSet connectors = GetConnectorManager(element)?.UnusedConnectors;
//            if (connectors == null || connectors.IsEmpty) return null;

//            Connector closest = null;
//            double minDist = double.MaxValue;

//            foreach (Connector c in connectors)
//            {
//                double dist = c.Origin.DistanceTo(point);
//                if (dist < minDist)
//                {
//                    minDist = dist;
//                    closest = c;
//                }
//            }
//            return closest;
//        }

//        public static ConnectorManager GetConnectorManager(Element element)
//        {
//            if (element is MEPCurve mepCurve)
//                return mepCurve.ConnectorManager;
//            if (element is FamilyInstance fi && fi.MEPModel != null)
//                return fi.MEPModel.ConnectorManager;
//            return null;
//        }
//    }

//    public static class SelectionFilters
//    {
//        public class NoInsulationFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is InsulationLiningBase) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }

//        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }
//    }
//}

