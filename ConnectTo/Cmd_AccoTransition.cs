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
        /// <summary>
        /// Main entry point for the ACCO Transition Command.
        /// Allows user to select two MEP elements and attempts to connect them with a transition fitting.
        /// Ensures angle constraints are respected. Allows multiple attempts in a single session.
        /// </summary>
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Loop until the user cancels (ESC) to allow repeated duct connections
                while (true)
                {
                    // this variables will hold the selected elements and their global points
                    Element elem1, elem2; // This will hold the selected elements.
                    XYZ xyz1, xyz2; // This will hold the global points of the selected elements. The global point is the point in the model space where the user clicked.

                    // Ask user to pick two distinct duct elements (movable and stationary)
                    // and output them to elem1, xyz1, elem2, xyz2 or break if user cancels
                    if (!TryPickTwoDistinctElements(uidoc, doc, out elem1, out xyz1, out elem2, out xyz2))
                        break;
                    #region "This code is commented out because it was replaced by TryPickTwoDistinctElements method to allow for retry on second pick if the same element is selected again."
                    //// Ask the user to pick the first duct element (movable)
                    //Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationNoFamilyInstanceFilter(),
                    //    "Pick element 1 (movable connector) or press ESC to finish");
                    //if (pick1 == null) break; // User cancelled selection

                    //Element elem1 = doc.GetElement(pick1);
                    //XYZ xyz1 = pick1.GlobalPoint;

                    //// Ask user to pick the second duct element (stationary)
                    //Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationFilter(),
                    //    "Pick element 2 (static connector)");
                    //Element elem2 = doc.GetElement(pick2);
                    //XYZ xyz2 = pick2.GlobalPoint;
                    #endregion

                    // Try to get the nearest unused connector from each selected element
                    Connector conn1 = ConnectorUtils.GetClosestUnusedConnector(elem1, xyz1);
                    Connector conn2 = ConnectorUtils.GetClosestUnusedConnector(elem2, xyz2);

                    // If either connector is missing, show error and loop again
                    if (conn1 == null || conn2 == null)
                    {
                        TaskDialog.Show("Cannot Proceed", "One of the selected elements does not have an open end.");// error
                        continue;
                    }

                    // Ensure both connectors belong to the same domain (e.g., HVAC)
                    //if (conn1.Domain != conn2.Domain)
                    //{
                    //    TaskDialog.Show("Cannot Proceed", "You must select 2 ducts.");
                    //    continue;
                    //}

                    // Try to find a valid fitting length based on allowed angles
                    bool userAcknowledgedAngleOverride = false;
                    int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

                    // If no valid length exists within allowed angle, ask user if they want to override
                    if (bestLengthInches == null)
                    {
                        TaskDialog td = new TaskDialog("Angle Constraint")
                        {
                            MainInstruction = "The fitting angle exceeds the 30° construction limit. Please review offset lengths or consider using elbows. If a transition angle greater than 30° is required, please consult the detailing department.",
                            MainContent = "Do you want to proceed anyway?",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };

                        if (td.Show() == TaskDialogResult.No)
                            continue; // User chose not to override, restart loop

                        userAcknowledgedAngleOverride = true;
                    }

                    // Begin Revit transaction to create the fitting
                    using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
                    {
                        tx.Start();

                        // Attempt to create a transition fitting between the connectors
                        FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
                        if (fittingInstance == null)
                        {
                            TaskDialog.Show("Cannot Proceed", "Failed to create transition fitting.");
                            tx.RollBack();
                            continue; // Try again
                        }

                        // If angle override was NOT allowed and a best length is known, try to set the correct fitting type
                        if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
                        {
                            // Get all available symbols from the same family as the placed fitting
                            List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
                                .OfClass(typeof(FamilySymbol))
                                .Cast<FamilySymbol>()
                                .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
                                .ToList();

                            // Convert the desired length to a string for name matching (e.g., "16")
                            string lengthStr = bestLengthInches.Value.ToString();

                            // Find a symbol whose name includes the desired length (e.g., ACCO 16")
                            FamilySymbol targetSymbol = availableSymbols
                                .FirstOrDefault(s => s.Name.Contains(lengthStr));

                            // If a matching symbol was found and is different from the current one, apply it
                            if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
                            {
                                fittingInstance.Symbol = targetSymbol;
                            }
                        }

                        tx.Commit();
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // User cancelled with ESC or closed prompt
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Any unexpected error will be displayed to user
                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Prompts the user to select two different elements (ducts or fittings),
        /// with retry on second pick if the same element is selected again.
        /// </summary>
        /// <param name="uidoc">Active UIDocument</param>
        /// <param name="doc">Active Document</param>
        /// <param name="elem1">First selected element (movable)</param>
        /// <param name="point1">Global point of first pick</param>
        /// <param name="elem2">Second selected element (static)</param>
        /// <param name="point2">Global point of second pick</param>
        /// <returns>True if two valid and distinct elements were selected; otherwise, false.</returns>
        private bool TryPickTwoDistinctElements(UIDocument uidoc, Document doc,
            out Element? elem1, out XYZ? point1,
            out Element? elem2, out XYZ? point2)
        {
            elem1 = null;
            point1 = null;
            elem2 = null;
            point2 = null;

            try
            {
                // First selection
                Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element,
                    new SelectionFilters.NoInsulationNoFamilyInstanceFilter(),
                    "Pick element 1 (movable connector) or press ESC to finish");

                if (pick1 == null)
                    return false;

                elem1 = doc.GetElement(pick1);
                point1 = pick1.GlobalPoint;

                while (true)
                {
                    Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element,
                        new SelectionFilters.NoInsulationFilter(),
                        "Pick element 2 (static connector)");

                    if (pick2 == null)
                        return false;

                    elem2 = doc.GetElement(pick2);
                    point2 = pick2.GlobalPoint;

                    if (elem2.Id == elem1.Id)
                    {
                        TaskDialog.Show("Action Required", "Please select two unique elements before proceeding.");
                        continue;
                    }

                    return true; // Valid second selection
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false;
            }
        }


        /// <summary>
        /// Returns the push button data used to display this command in the ribbon.
        /// </summary>
        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnAccoTransition";
            string buttonTitle = "Transition\nTwo Ducts";

            // This helper wraps Revit push button creation and icon assignment
            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Blue_32,
                Properties.Resources.Blue_16,
                "This button adds a transition fitting between two open ended ducts with a transition length where angles are less than 30°."
            );

            return myButtonData1.Data;
        }
    }

    /// <summary>
    /// Contains helper methods for evaluating transition fitting angles and lengths.
    /// </summary>
    public static class FittingEvaluator
    {
        /// <summary>
        /// Determines the shortest standard fitting length (in inches) that satisfies maximum angle constraints
        /// between two connectors, based on duct geometry and spacing.
        /// </summary>
        /// <param name="conn1">First MEP connector (usually movable).</param>
        /// <param name="conn2">Second MEP connector (usually stationary).</param>
        /// <returns>Shortest valid transition length in inches, or null if all lengths exceed angle limit.</returns>
        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
        {
            // Ensure both connectors are of supported types: rectangular, round, or oval
            if (!IsShapeSupported(conn1) || !IsShapeSupported(conn2))
            {
                TaskDialog.Show("Cannot Proceed", "One or both ducts have unsupported shapes. Only rectangular, round, and oval are supported.");
                return null;
            }

            // Calculate offset vector between connector origins
            XYZ centerOffset = conn2.Origin - conn1.Origin;

            // Project that offset along the connector's local X and Y axes
            double offsetX = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
            double offsetY = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

            // Get dimensions of each connector's cross-section
            double w1 = GetEffectiveWidth(conn1);
            double h1 = GetEffectiveHeight(conn1);
            double w2 = GetEffectiveWidth(conn2);
            double h2 = GetEffectiveHeight(conn2);

            // Calculate the slope rise components in X and Y directions
            double rise1 = offsetX + Math.Abs(w2 / 2.0 - w1 / 2.0);
            double rise2 = offsetY + Math.Abs(h2 / 2.0 - h1 / 2.0);

            // Evaluate each standard length to find the shortest valid option
            foreach (int lenInches in TransitionSettings.LengthsInches)
            {
                double lenFeet = lenInches / 12.0;
                if (lenFeet <= 0) continue;

                // Calculate angles from rise/run (converted to degrees)
                double angle1 = Math.Atan(rise1 / lenFeet) * (180.0 / Math.PI);
                double angle2 = Math.Atan(rise2 / lenFeet) * (180.0 / Math.PI);

                // Check if both X and Y directional slopes are within limit
                if (angle1 <= TransitionSettings.MaxAngleDeg && angle2 <= TransitionSettings.MaxAngleDeg)
                    return lenInches;
            }

            // No valid length found
            return null;
        }

        /// <summary>
        /// Retrieves the effective width for any supported connector shape.
        /// </summary>
        private static double GetEffectiveWidth(Connector c)
        {
            return c.Shape switch
            {
                ConnectorProfileType.Round => c.Radius * 2,
                ConnectorProfileType.Rectangular => c.Width,
                ConnectorProfileType.Oval => c.Width,
                _ => throw new InvalidOperationException("Unsupported connector shape for width evaluation.")
            };
        }

        /// <summary>
        /// Retrieves the effective height for any supported connector shape.
        /// </summary>
        private static double GetEffectiveHeight(Connector c)
        {
            return c.Shape switch
            {
                ConnectorProfileType.Round => c.Radius * 2,
                ConnectorProfileType.Rectangular => c.Height,
                ConnectorProfileType.Oval => c.Height,
                _ => throw new InvalidOperationException("Unsupported connector shape for height evaluation.")
            };
        }

        /// <summary>
        /// Validates that the connector shape is round, rectangular, or oval.
        /// </summary>
        private static bool IsShapeSupported(Connector conn)
        {
            return conn.Shape == ConnectorProfileType.Rectangular ||
                   conn.Shape == ConnectorProfileType.Round ||
                   conn.Shape == ConnectorProfileType.Oval;
        }
    }

    /// <summary>
    /// Configuration for angle constraints and supported fitting lengths.
    /// </summary>
    public static class TransitionSettings
    {
        /// <summary>
        /// Maximum allowed fitting angle in degrees.
        /// </summary>
        public static double MaxAngleDeg = 30.0;

        /// <summary>
        /// List of standard duct fitting lengths to evaluate (in inches).
        /// </summary>
        public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };
    }

    /// <summary>
    /// Utility functions to work with connectors.
    /// </summary>
    public static class ConnectorUtils
    {
        /// <summary>
        /// Returns the nearest unused connector to a selected point on an element.
        /// </summary>
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

        /// <summary>
        /// Retrieves the ConnectorManager from supported element types (MEPCurve or FamilyInstance).
        /// </summary>
        public static ConnectorManager GetConnectorManager(Element element)
        {
            if (element is MEPCurve mepCurve)
                return mepCurve.ConnectorManager;
            if (element is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;
            return null;
        }
    }

    /// <summary>
    /// Filters that restrict element selection in the UI.
    /// </summary>
    public static class SelectionFilters
    {
        /// <summary>
        /// Filter for ducts with no insulation.
        /// </summary>
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

        /// <summary>
        /// Filter for ducts with no insulation and not family instances.
        /// </summary>
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
}

#region working verion with only comments on execute method
//namespace ConnectTo
//{
//    [Transaction(TransactionMode.Manual)]
//    public class Cmd_AccoTransition : IExternalCommand
//    {
//        /// <summary>
//        /// Main entry point for the ACCO Transition Command.
//        /// Allows user to select two MEP elements and attempts to connect them with a transition fitting.
//        /// Ensures angle constraints are respected. Allows multiple attempts in a single session.
//        /// </summary>
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIApplication uiapp = commandData.Application;
//            UIDocument uidoc = uiapp.ActiveUIDocument;
//            Document doc = uidoc.Document;

//            try
//            {
//                // Loop until the user cancels (ESC) to allow repeated duct connections
//                while (true)
//                {
//                    // Ask user to pick the first duct element (movable)
//                    Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationNoFamilyInstanceFilter(),
//                        "Pick element 1 (movable connector) or press ESC to finish");
//                    if (pick1 == null) break; // User cancelled selection

//                    Element elem1 = doc.GetElement(pick1);
//                    XYZ xyz1 = pick1.GlobalPoint;

//                    // Ask user to pick the second duct element (stationary)
//                    Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element, new SelectionFilters.NoInsulationFilter(),
//                        "Pick element 2 (static connector)");
//                    Element elem2 = doc.GetElement(pick2);
//                    XYZ xyz2 = pick2.GlobalPoint;

//                    // Try to get the nearest unused connector from each selected element
//                    Connector conn1 = ConnectorUtils.GetClosestUnusedConnector(elem1, xyz1);
//                    Connector conn2 = ConnectorUtils.GetClosestUnusedConnector(elem2, xyz2);

//                    // If either connector is missing, show error and loop again
//                    if (conn1 == null || conn2 == null)
//                    {
//                        TaskDialog.Show("Error", "One of the selected elements has no unused connector.");
//                        continue;
//                    }

//                    // Ensure both connectors belong to the same domain (e.g., HVAC)
//                    if (conn1.Domain != conn2.Domain)
//                    {
//                        TaskDialog.Show("Domain Error", "Connectors are from different domains.");
//                        continue;
//                    }

//                    // Try to find a valid fitting length based on allowed angles
//                    bool userAcknowledgedAngleOverride = false;
//                    int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

//                    // If no valid length exists within allowed angle, ask user if they want to override
//                    if (bestLengthInches == null)
//                    {
//                        TaskDialog td = new TaskDialog("Angle Constraint")
//                        {
//                            MainInstruction = "The fitting angle exceeds the 30° construction limit. Please review the offset lengths or consider using elbows. If a transition angle greater than 30° is required, you must consult with the detailing department.",
//                            MainContent = "Do you want to proceed anyway?",
//                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                            DefaultButton = TaskDialogResult.No
//                        };

//                        if (td.Show() == TaskDialogResult.No)
//                            continue; // User chose not to override, restart loop

//                        userAcknowledgedAngleOverride = true;
//                    }

//                    // Begin Revit transaction to create the fitting
//                    using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
//                    {
//                        tx.Start();

//                        // Attempt to create a transition fitting between the connectors
//                        FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
//                        if (fittingInstance == null)
//                        {
//                            TaskDialog.Show("Error", "Failed to create transition fitting.");
//                            tx.RollBack();
//                            continue; // Try again
//                        }

//                        // If angle override was NOT allowed and a best length is known, try to set the correct fitting type
//                        if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
//                        {
//                            // Get all available symbols from the same family as the placed fitting
//                            List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
//                                .OfClass(typeof(FamilySymbol))
//                                .Cast<FamilySymbol>()
//                                .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
//                                .ToList();

//                            // Convert the desired length to a string for name matching (e.g., "16")
//                            string lengthStr = bestLengthInches.Value.ToString();

//                            // Find a symbol whose name includes the desired length (e.g., ACCO 16")
//                            FamilySymbol targetSymbol = availableSymbols
//                                .FirstOrDefault(s => s.Name.Contains(lengthStr));

//                            // If a matching symbol was found and is different from the current one, apply it
//                            if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
//                            {
//                                fittingInstance.Symbol = targetSymbol;
//                            }
//                        }

//                        tx.Commit();
//                    }
//                }
//            }
//            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
//            {
//                // User cancelled with ESC or closed prompt
//                return Result.Succeeded;
//            }
//            catch (Exception ex)
//            {
//                // Any unexpected error will be displayed to user
//                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
//                return Result.Failed;
//            }

//            return Result.Succeeded;
//        }

//        /// <summary>
//        /// Returns the push button data used to display this command in the ribbon.
//        /// </summary>
//        internal static PushButtonData GetButtonData()
//        {
//            string buttonInternalName = "btnAccoTransition";
//            string buttonTitle = "ACCO Transition";

//            // This helper wraps Revit push button creation and icon assignment
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


//public static class FittingEvaluator
//{
//    public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
//    {
//        XYZ centerOffset = conn2.Origin - conn1.Origin;
//        double offsetX = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
//        double offsetY = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

//        double w1 = GetEffectiveWidth(conn1);
//        double h1 = GetEffectiveHeight(conn1);
//        double w2 = GetEffectiveWidth(conn2);
//        double h2 = GetEffectiveHeight(conn2);

//        double rise1 = offsetX + Math.Abs(w2 / 2.0 - w1 / 2.0);
//        double rise2 = offsetY + Math.Abs(h2 / 2.0 - h1 / 2.0);

//        foreach (int lenInches in TransitionSettings.LengthsInches)
//        {
//            double lenFeet = lenInches / 12.0;
//            if (lenFeet <= 0) continue;

//            double angle1 = Math.Atan(rise1 / lenFeet) * (180.0 / Math.PI);
//            double angle2 = Math.Atan(rise2 / lenFeet) * (180.0 / Math.PI);

//            if (angle1 <= TransitionSettings.MaxAngleDeg && angle2 <= TransitionSettings.MaxAngleDeg)
//                return lenInches;
//        }

//        return null;
//    }

//    private static double GetEffectiveWidth(Connector c)
//    {
//        return c.Shape switch
//        {
//            ConnectorProfileType.Round => c.Radius * 2,
//            ConnectorProfileType.Rectangular => c.Width,
//            ConnectorProfileType.Oval => c.Width,
//            _ => throw new InvalidOperationException("Unsupported connector shape for width evaluation.")
//        };
//    }

//    private static double GetEffectiveHeight(Connector c)
//    {
//        return c.Shape switch
//        {
//            ConnectorProfileType.Round => c.Radius * 2,
//            ConnectorProfileType.Rectangular => c.Height,
//            ConnectorProfileType.Oval => c.Height,
//            _ => throw new InvalidOperationException("Unsupported connector shape for height evaluation.")
//        };
//    }
//}

//public static class TransitionSettings
//{
//    public static double MaxAngleDeg = 30.0;
//    public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };
//}

//public static class ConnectorUtils
//{
//    public static Connector GetClosestUnusedConnector(Element element, XYZ point)
//    {
//        ConnectorSet connectors = GetConnectorManager(element)?.UnusedConnectors;
//        if (connectors == null || connectors.IsEmpty) return null;

//        Connector closest = null;
//        double minDist = double.MaxValue;

//        foreach (Connector c in connectors)
//        {
//            double dist = c.Origin.DistanceTo(point);
//            if (dist < minDist)
//            {
//                minDist = dist;
//                closest = c;
//            }
//        }
//        return closest;
//    }

//    public static ConnectorManager GetConnectorManager(Element element)
//    {
//        if (element is MEPCurve mepCurve)
//            return mepCurve.ConnectorManager;
//        if (element is FamilyInstance fi && fi.MEPModel != null)
//            return fi.MEPModel.ConnectorManager;
//        return null;
//    }
//}

//public static class SelectionFilters
//{
//    public class NoInsulationFilter : ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//        {
//            if (elem is not Duct) return false;
//            if (elem is InsulationLiningBase) return false;
//            return ConnectorUtils.GetConnectorManager(elem) != null;
//        }
//        public bool AllowReference(Reference reference, XYZ position) => true;
//    }

//    public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//        {
//            if (elem is not Duct) return false;
//            if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//            return ConnectorUtils.GetConnectorManager(elem) != null;
//        }
//        public bool AllowReference(Reference reference, XYZ position) => true;
//    }
//}
//}

#endregion

#region best working version for all duct senarios with no comments.
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

//                bool userAcknowledgedAngleOverride = false;
//                int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

//                if (bestLengthInches == null)
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = "The fitting angle exceeds the 30° construction limit. Please review the offset lengths or consider using elbows. If a transition angle greater than 30° is required, you must consult with the detailing department.",
//                        MainContent = "Do you want to proceed anyway?",
//                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                        DefaultButton = TaskDialogResult.No
//                    };

//                    if (td.Show() == TaskDialogResult.No)
//                        return Result.Cancelled;

//                    userAcknowledgedAngleOverride = true;
//                }

//                using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
//                {
//                    tx.Start();

//                    FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
//                    if (fittingInstance == null)
//                    {
//                        TaskDialog.Show("Error", "Failed to create transition fitting.");
//                        tx.RollBack();
//                        return Result.Failed;
//                    }

//                    if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
//                    {
//                        List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
//                            .OfClass(typeof(FamilySymbol))
//                            .Cast<FamilySymbol>()
//                            .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
//                            .ToList();

//                        string lengthStr = bestLengthInches.Value.ToString();

//                        FamilySymbol targetSymbol = availableSymbols
//                            .FirstOrDefault(s => s.Name.Contains(lengthStr));

//                        if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
//                        {
//                            fittingInstance.Symbol = targetSymbol;
//                        }
//                    }

//                    tx.Commit();
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


//    public static class FittingEvaluator
//    {
//        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
//        {
//            XYZ centerOffset = conn2.Origin - conn1.Origin;
//            double offsetX = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
//            double offsetY = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

//            double w1 = GetEffectiveWidth(conn1);
//            double h1 = GetEffectiveHeight(conn1);
//            double w2 = GetEffectiveWidth(conn2);
//            double h2 = GetEffectiveHeight(conn2);

//            double rise1 = offsetX + Math.Abs(w2 / 2.0 - w1 / 2.0);
//            double rise2 = offsetY + Math.Abs(h2 / 2.0 - h1 / 2.0);

//            foreach (int lenInches in TransitionSettings.LengthsInches)
//            {
//                double lenFeet = lenInches / 12.0;
//                if (lenFeet <= 0) continue;

//                double angle1 = Math.Atan(rise1 / lenFeet) * (180.0 / Math.PI);
//                double angle2 = Math.Atan(rise2 / lenFeet) * (180.0 / Math.PI);

//                if (angle1 <= TransitionSettings.MaxAngleDeg && angle2 <= TransitionSettings.MaxAngleDeg)
//                    return lenInches;
//            }

//            return null;
//        }

//        private static double GetEffectiveWidth(Connector c)
//        {
//            return c.Shape switch
//            {
//                ConnectorProfileType.Round => c.Radius * 2,
//                ConnectorProfileType.Rectangular => c.Width,
//                ConnectorProfileType.Oval => c.Width,
//                _ => throw new InvalidOperationException("Unsupported connector shape for width evaluation.")
//            };
//        }

//        private static double GetEffectiveHeight(Connector c)
//        {
//            return c.Shape switch
//            {
//                ConnectorProfileType.Round => c.Radius * 2,
//                ConnectorProfileType.Rectangular => c.Height,
//                ConnectorProfileType.Oval => c.Height,
//                _ => throw new InvalidOperationException("Unsupported connector shape for height evaluation.")
//            };
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
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }

//        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }
//    }
//}
#endregion

#region this version works with retangles, ovals and round ducts of the same shape
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

//                bool userAcknowledgedAngleOverride = false;
//                int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

//                if (bestLengthInches == null)
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = "The fitting angle exceeds the 30° construction limit. Please review the offset lengths or consider using elbows. If a transition angle greater than 30° is required, you must consult with the detailing department.",
//                        MainContent = "Do you want to proceed anyway?",
//                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                        DefaultButton = TaskDialogResult.No
//                    };

//                    if (td.Show() == TaskDialogResult.No)
//                        return Result.Cancelled;

//                    userAcknowledgedAngleOverride = true;
//                }

//                using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
//                {
//                    tx.Start();

//                    FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
//                    if (fittingInstance == null)
//                    {
//                        TaskDialog.Show("Error", "Failed to create transition fitting.");
//                        tx.RollBack();
//                        return Result.Failed;
//                    }

//                    if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
//                    {
//                        List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
//                            .OfClass(typeof(FamilySymbol))
//                            .Cast<FamilySymbol>()
//                            .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
//                            .ToList();

//                        string lengthStr = bestLengthInches.Value.ToString();

//                        FamilySymbol targetSymbol = availableSymbols
//                            .FirstOrDefault(s => s.Name.Contains(lengthStr));

//                        if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
//                        {
//                            fittingInstance.Symbol = targetSymbol;
//                        }
//                    }

//                    tx.Commit();
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

//    public static class FittingEvaluator
//    {
//        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
//        {
//            if (conn1.Shape != conn2.Shape)
//            {
//                TaskDialog.Show("Unsupported Type", "This function only supports connectors of the same shape.");
//                return null;
//            }

//            XYZ centerOffset = conn2.Origin - conn1.Origin;
//            double offsetX = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
//            double offsetY = Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

//            double rise1 = 0;
//            double rise2 = 0;

//            try
//            {
//                switch (conn1.Shape)
//                {
//                    case ConnectorProfileType.Rectangular:
//                        double halfWidthDiff = Math.Abs(conn2.Width - conn1.Width) / 2.0;
//                        double halfHeightDiff = Math.Abs(conn2.Height - conn1.Height) / 2.0;
//                        rise1 = offsetX + halfWidthDiff;
//                        rise2 = offsetY + halfHeightDiff;
//                        break;

//                    case ConnectorProfileType.Round:
//                        double radiusDiffR = Math.Abs(conn2.Radius - conn1.Radius);
//                        rise1 = offsetX + radiusDiffR;
//                        rise2 = offsetY + radiusDiffR;
//                        break;

//                    case ConnectorProfileType.Oval:
//                        double widthDiff = Math.Abs(conn2.Width - conn1.Width) / 2.0;
//                        double heightDiff = Math.Abs(conn2.Height - conn1.Height) / 2.0;
//                        rise1 = offsetX + widthDiff;
//                        rise2 = offsetY + heightDiff;
//                        break;

//                    default:
//                        TaskDialog.Show("Unsupported Type", $"Connector shape '{conn1.Shape}' is not supported.");
//                        return null;
//                }
//            }
//            catch (Exception ex)
//            {
//                TaskDialog.Show("Evaluation Error", $"An error occurred while evaluating connector shapes: {ex.Message}");
//                return null;
//            }

//            foreach (int lenInches in TransitionSettings.LengthsInches)
//            {
//                double lenFeet = lenInches / 12.0;
//                if (lenFeet <= 0) continue;

//                double angle1 = Math.Atan(rise1 / lenFeet) * (180.0 / Math.PI);
//                double angle2 = Math.Atan(rise2 / lenFeet) * (180.0 / Math.PI);

//                if (angle1 <= TransitionSettings.MaxAngleDeg && angle2 <= TransitionSettings.MaxAngleDeg)
//                    return lenInches;
//            }

//            return null;
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
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }

//        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }
//    }
//}

#endregion

#region working version with angle calculation
//namespace ConnectTo
//{
//    /*
//This prompt is designed to be a complete technical specification, detailing the project structure, class responsibilities, and the specific logic required for each method, including the final, correct face-based angle calculation.

//---
//## Prompt for AI:

//Create a complete C# Revit Add-in for placing rectangular duct transitions. The add-in should be contained within a single C# file.

//### 1. Overall Goal

//The tool will be a Revit command that allows a user to select two rectangular duct elements. It will then automatically place the shortest possible standard-length transition fitting that connects them, ensuring the angles of the fitting's faces do not exceed a predefined maximum (e.g., 30 degrees).

//### 2. Project Structure

//The solution should be organized into the following static and public classes within a single namespace (e.g., `ConnectTo`):

//* `Cmd_AccoTransition`: The main `IExternalCommand` class.
//* `FittingEvaluator`: A static helper class to calculate the required fitting length.
//* `TransitionSettings`: A static class to hold configuration data.
//* `ConnectorUtils`: A static helper class for finding MEP connectors.
//* `SelectionFilters`: A static class containing two `ISelectionFilter` implementations.

//### 3. Detailed Class Requirements

//#### **`Cmd_AccoTransition` Class**

//* This class must implement the `IExternalCommand` interface.
//* **`Execute` Method Logic:**
//    1.  Get the `UIDocument` and `Document` objects.
//    2.  Prompt the user to select the first element using `uidoc.Selection.PickObject`. Use the `NoInsulationNoFamilyInstanceFilter`.
//    3.  Prompt the user to select the second element. Use the `NoInsulationFilter`.
//    4.  For each selection, find the nearest unused connector using `ConnectorUtils.GetClosestUnusedConnector`.
//    5.  **Validation**:
//        * If either connector is null, show a `TaskDialog` error and cancel.
//        * If the connectors' `Domain` properties do not match, show an error and cancel.
//    6.  Call `FittingEvaluator.GetShortestValidLengthInches` to determine the best length.
//    7.  **Angle Constraint Handling**:
//        * If the result is `null`, display a `TaskDialog` asking the user: "All transition types exceed 30° angle limit. Do you want to proceed anyway?".
//        * If the user selects "No", cancel the command.
//    8.  **Transaction Logic**:
//        * Start a transaction named "Place ACCO Transition".
//        * Create the fitting using `doc.Create.NewTransitionFitting(conn1, conn2)`. Handle the case where this returns null.
//        * If a valid length was found (and the user didn't override the angle warning), perform the following:
//            * Get all `FamilySymbol` types from the newly created fitting's family.
//            * Find the first `FamilySymbol` whose name contains the string representation of the calculated length (e.g., search for "16" if the length is 16).
//            * If a matching `FamilySymbol` is found and it's different from the current one, change the fitting's type by setting `fittingInstance.Symbol = targetSymbol;`.
//            * Call `doc.Regenerate()` to apply the geometry changes.
//        * Commit the transaction.
//    9.  Include standard `try/catch` blocks for `OperationCanceledException` and general exceptions.

//---
//#### **`FittingEvaluator` Class**

//* This static class will contain one public method: `public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)`.
//* **Method Logic (Face-Based Calculation):**
//    1.  First, validate that both connectors have a `Shape` of `ConnectorProfileType.Rectangular`. If not, show a `TaskDialog` error and return `null`.
//    2.  Calculate the vector offset between the two connector origins: `centerOffset = conn2.Origin - conn1.Origin`.
//    3.  Calculate the difference in duct dimensions: `halfWidthDiff = Abs(conn2.Width - conn1.Width) / 2` and `halfHeightDiff = Abs(conn2.Height - conn1.Height) / 2`.
//    4.  Resolve the center offset along the local axes of the first connector: `offsetX = Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX))` and `offsetY = Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY))`.
//    5.  Calculate the total **rise** for the horizontal (left/right) and vertical (top/bottom) faces:
//        * `horizontalRise = offsetX + halfWidthDiff`
//        * `verticalRise = offsetY + halfHeightDiff`
//    6.  Iterate through each `lenInches` in `TransitionSettings.LengthsInches`. For each length:
//        * Convert `lenInches` to `lenFeet`. This is the **run**.
//        * Calculate `horizontalAngle = arctan(horizontalRise / lenFeet)`.
//        * Calculate `verticalAngle = arctan(verticalRise / lenFeet)`.
//        * Convert both angles to degrees.
//        * If both `horizontalAngle` and `verticalAngle` are less than or equal to `TransitionSettings.MaxAngleDeg`, **return `lenInches`**.
//    7.  If the loop completes without finding a valid length, **return `null`**.

//---
//#### **`TransitionSettings` Class**

//* This static class holds two public static fields:
//    * `public static double MaxAngleDeg = 30.0;`
//    * `public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };` (You can add more lengths as needed).

//---
//#### **`ConnectorUtils` Class**

//* This static class holds two helper methods:
//    1.  `public static Connector GetClosestUnusedConnector(Element element, XYZ point)`: Iterates through the element's `UnusedConnectors` and returns the one with the minimum `DistanceTo` the provided point.
//    2.  `public static ConnectorManager GetConnectorManager(Element element)`: Safely gets the `ConnectorManager` from an element, checking if it's an `MEPCurve` or a `FamilyInstance` with an `MEPModel`.

//---
//#### **`SelectionFilters` Class**

//* This static class contains two nested public classes that implement `ISelectionFilter`:
//    1.  `NoInsulationFilter`: The `AllowElement` method should return `true` only for `Duct` elements that are not `InsulationLiningBase`.
//    2.  `NoInsulationNoFamilyInstanceFilter`: The `AllowElement` method should return `true` only for `Duct` elements that are not `InsulationLiningBase` or `FamilyInstance`.
//     */

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
//                XYZ xyz1 = pick1.GlobalPoint; // The point where the user clicked

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

//                bool userAcknowledgedAngleOverride = false;
//                //double? bestLengthFeet = FittingEvaluator.GetShortestValidLengthFeetByCornerOffsets(conn1, conn2);
//                int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

//                if (bestLengthInches == null)
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = " The fitting angle exceeds the 30° construction limit. Please review the offset lengths or consider using elbows. If a transition angle greater than 30° is required, you must consult with the detailing department.",
//                        MainContent = "Do you want to proceed anyway?",
//                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                        DefaultButton = TaskDialogResult.No
//                    };

//                    if (td.Show() == TaskDialogResult.No)
//                        return Result.Cancelled;

//                    userAcknowledgedAngleOverride = true;
//                }


//                using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
//                {
//                    tx.Start();

//                    FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
//                    if (fittingInstance == null)
//                    {
//                        TaskDialog.Show("Error", "Failed to create transition fitting.");
//                        tx.RollBack();
//                        return Result.Failed;
//                    }

//                    if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
//                    {
//                        // --- REFACTORED LOGIC ---

//                        // 1. Get all symbol types from the fitting's family ONCE.
//                        //    Using .ToList() executes the query and stores results in memory.
//                        List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
//                            .OfClass(typeof(FamilySymbol))
//                            .Cast<FamilySymbol>()
//                            .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
//                            .ToList();

//                        // Convert the integer length to a string for searching. 12 -> "12"
//                        string lengthStr = bestLengthInches.Value.ToString();

//                        // 2. Search the list we already created. This is much faster.
//                        FamilySymbol targetSymbol = availableSymbols
//                            .FirstOrDefault(s => s.Name.Contains(lengthStr));

//                        if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
//                        {
//                            fittingInstance.Symbol = targetSymbol;
//                        }
//                    }

//                    tx.Commit();
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

//    public static class FittingEvaluator
//    {
//        /// <summary>
//        /// Calculates the shortest valid transition length by checking the slope of the four faces.
//        /// This method matches how Revit calculates transition angles.
//        /// </summary>
//        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
//        {
//            if (conn1.Shape != ConnectorProfileType.Rectangular ||
//                conn2.Shape != ConnectorProfileType.Rectangular)
//            {
//                TaskDialog.Show("Unsupported Type", "This function currently only supports rectangular duct connectors.");
//                return null;
//            }

//            // --- Calculate the total offset for each of the four faces ---

//            // The vector from the center of connector 1 to the center of connector 2
//            XYZ centerOffset = conn2.Origin - conn1.Origin;

//            // The change in size for each half of the duct
//            double halfWidthDiff = System.Math.Abs(conn2.Width - conn1.Width) / 2.0;
//            double halfHeightDiff = System.Math.Abs(conn2.Height - conn1.Height) / 2.0;

//            // The offset of the duct centers, resolved along the connector's local X and Y axes
//            double offsetX = System.Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
//            double offsetY = System.Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

//            // The "rise" for the horizontal (left/right) and vertical (top/bottom) faces
//            double horizontalRise = offsetX + halfWidthDiff;
//            double verticalRise = offsetY + halfHeightDiff;


//            // --- Find the shortest length that satisfies the angle constraints ---

//            foreach (int lenInches in TransitionSettings.LengthsInches)
//            {
//                double lenFeet = lenInches / 12.0;
//                if (lenFeet <= 0) continue;

//                // Calculate the angle for the horizontal and vertical faces
//                double horizontalAngle = System.Math.Atan(horizontalRise / lenFeet) * (180.0 / System.Math.PI);
//                double verticalAngle = System.Math.Atan(verticalRise / lenFeet) * (180.0 / System.Math.PI);

//                // Check if both resulting angles are within the allowed maximum
//                if (horizontalAngle <= TransitionSettings.MaxAngleDeg &&
//                    verticalAngle <= TransitionSettings.MaxAngleDeg)
//                {
//                    // This is the shortest valid length
//                    return lenInches;
//                }
//            }

//            // No available length could satisfy the constraints
//            return null;
//        }
//    }

//    public static class TransitionSettings
//    {
//        public static double MaxAngleDeg = 30.0;
//        public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };
//    }

//    public static class ConnectorUtils
//    {
//        /// <summary>
//        /// Finds the closest unused connector to a given point on an element.
//        /// it takes an `Element` and a `XYZ` point, and returns the closest unused `Connector`.
//        /// the XYZ point is typically the point where the user clicked to select the element.
//        /// </summary>
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

//        /// <summary>
//        /// The connector manager is used to access the connectors of an element.
//        /// This method will chech if the element is an `MEPCurve` or a `FamilyInstance` with an `MEPModel`.
//        /// and return the `ConnectorManager` if available.
//        /// </summary>
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
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }

//        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }
//    }
//}
#endregion End Of working version with angle calculation

#region tested working version - iterates through all available fitting types and checks angles
// ########################### tested working version - iterates through all available fitting types and checks angles
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
//    ///<summary>
//    /// Revit external command that creates a transition fitting (e.g., duct transition) between two user-selected duct elements.
//    /// The command prompts the user to select two ducts, finds the closest unused connectors on each, and attempts to create a transition fitting between them.
//    /// It evaluates all available fitting types in the family, assigning the first type where all angle parameters ("Angle Top", "Angle Bottom", "Angle Left", "Angle Right")
//    /// are within the maximum allowed angle (default 30°). If no suitable type is found, the user is prompted to keep or delete the default fitting.
//    /// The command ensures only valid duct elements are selectable, checks connector compatibility, and handles all Revit transaction requirements.
//    /// </summary>
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

//                if (FittingEvaluator.TryFindBestFitting(doc, fittingInstance, out FamilySymbol bestSymbol))
//                {
//                    using (Transaction tx = new Transaction(doc, "Assign Best Fitting Type"))
//                    {
//                        tx.Start();
//                        fittingInstance.Symbol = bestSymbol;
//                        tx.Commit();
//                    }
//                }
//                else
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = "All transition types exceed 30° angle limit.",
//                        MainContent = "Do you want to keep the default fitting anyway?",
//                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                        DefaultButton = TaskDialogResult.No
//                    };

//                    if (td.Show() == TaskDialogResult.No)
//                    {
//                        using (Transaction tx = new Transaction(doc, "Delete Fitting"))
//                        {
//                            tx.Start();
//                            doc.Delete(fittingInstance.Id);
//                            tx.Commit();
//                        }
//                        return Result.Cancelled;
//                    }
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

//    public static class FittingEvaluator
//    {
//        public static bool TryFindBestFitting(Document doc, FamilyInstance fittingInstance, out FamilySymbol? bestSymbol)
//        {
//            bestSymbol = null;

//            Family family = fittingInstance.Symbol.Family;
//            IEnumerable<FamilySymbol> symbols = new FilteredElementCollector(doc)
//                .OfClass(typeof(FamilySymbol))
//                .Cast<FamilySymbol>()
//                .Where(s => s.Family.Id == family.Id)
//                .OrderBy(s => TransitionSettings.LengthsInches.IndexOf(GetLengthFromName(s.Name)));

//            foreach (FamilySymbol symbol in symbols)
//            {
//                using (Transaction tx = new Transaction(doc, "Switch Transition Type"))
//                {
//                    tx.Start();
//                    fittingInstance.Symbol = symbol;
//                    tx.Commit();
//                }

//                double angleTop = GetAngleParameterValue(fittingInstance, "Angle Top");
//                double angleBottom = GetAngleParameterValue(fittingInstance, "Angle Bottom");
//                double angleLeft = GetAngleParameterValue(fittingInstance, "Angle Left");
//                double angleRight = GetAngleParameterValue(fittingInstance, "Angle Right");

//                if (Math.Abs(angleTop) <= TransitionSettings.MaxAngleDeg &&
//                    Math.Abs(angleBottom) <= TransitionSettings.MaxAngleDeg &&
//                    Math.Abs(angleLeft) <= TransitionSettings.MaxAngleDeg &&
//                    Math.Abs(angleRight) <= TransitionSettings.MaxAngleDeg)
//                {
//                    bestSymbol = symbol;
//                    return true;
//                }
//            }

//            return false;
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

//    // This class is used to allow the selections of DuctTypes only.
//    public static class SelectionFilters
//    {
//        public class NoInsulationFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }

//        public class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
//        {
//            public bool AllowElement(Element elem)
//            {
//                if (elem is not Duct) return false;
//                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
//                return ConnectorUtils.GetConnectorManager(elem) != null;
//            }
//            public bool AllowReference(Reference reference, XYZ position) => true;
//        }
//    }
//}
#endregion End Of tested working version - iterates through all available fitting types and checks angles