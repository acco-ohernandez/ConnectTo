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
    /*
This prompt is designed to be a complete technical specification, detailing the project structure, class responsibilities, and the specific logic required for each method, including the final, correct face-based angle calculation.

---
## Prompt for AI:

Create a complete C# Revit Add-in for placing rectangular duct transitions. The add-in should be contained within a single C# file.

### 1. Overall Goal

The tool will be a Revit command that allows a user to select two rectangular duct elements. It will then automatically place the shortest possible standard-length transition fitting that connects them, ensuring the angles of the fitting's faces do not exceed a predefined maximum (e.g., 30 degrees).

### 2. Project Structure

The solution should be organized into the following static and public classes within a single namespace (e.g., `ConnectTo`):

* `Cmd_AccoTransition`: The main `IExternalCommand` class.
* `FittingEvaluator`: A static helper class to calculate the required fitting length.
* `TransitionSettings`: A static class to hold configuration data.
* `ConnectorUtils`: A static helper class for finding MEP connectors.
* `SelectionFilters`: A static class containing two `ISelectionFilter` implementations.

### 3. Detailed Class Requirements

#### **`Cmd_AccoTransition` Class**

* This class must implement the `IExternalCommand` interface.
* **`Execute` Method Logic:**
    1.  Get the `UIDocument` and `Document` objects.
    2.  Prompt the user to select the first element using `uidoc.Selection.PickObject`. Use the `NoInsulationNoFamilyInstanceFilter`.
    3.  Prompt the user to select the second element. Use the `NoInsulationFilter`.
    4.  For each selection, find the nearest unused connector using `ConnectorUtils.GetClosestUnusedConnector`.
    5.  **Validation**:
        * If either connector is null, show a `TaskDialog` error and cancel.
        * If the connectors' `Domain` properties do not match, show an error and cancel.
    6.  Call `FittingEvaluator.GetShortestValidLengthInches` to determine the best length.
    7.  **Angle Constraint Handling**:
        * If the result is `null`, display a `TaskDialog` asking the user: "All transition types exceed 30° angle limit. Do you want to proceed anyway?".
        * If the user selects "No", cancel the command.
    8.  **Transaction Logic**:
        * Start a transaction named "Place ACCO Transition".
        * Create the fitting using `doc.Create.NewTransitionFitting(conn1, conn2)`. Handle the case where this returns null.
        * If a valid length was found (and the user didn't override the angle warning), perform the following:
            * Get all `FamilySymbol` types from the newly created fitting's family.
            * Find the first `FamilySymbol` whose name contains the string representation of the calculated length (e.g., search for "16" if the length is 16).
            * If a matching `FamilySymbol` is found and it's different from the current one, change the fitting's type by setting `fittingInstance.Symbol = targetSymbol;`.
            * Call `doc.Regenerate()` to apply the geometry changes.
        * Commit the transaction.
    9.  Include standard `try/catch` blocks for `OperationCanceledException` and general exceptions.

---
#### **`FittingEvaluator` Class**

* This static class will contain one public method: `public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)`.
* **Method Logic (Face-Based Calculation):**
    1.  First, validate that both connectors have a `Shape` of `ConnectorProfileType.Rectangular`. If not, show a `TaskDialog` error and return `null`.
    2.  Calculate the vector offset between the two connector origins: `centerOffset = conn2.Origin - conn1.Origin`.
    3.  Calculate the difference in duct dimensions: `halfWidthDiff = Abs(conn2.Width - conn1.Width) / 2` and `halfHeightDiff = Abs(conn2.Height - conn1.Height) / 2`.
    4.  Resolve the center offset along the local axes of the first connector: `offsetX = Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX))` and `offsetY = Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY))`.
    5.  Calculate the total **rise** for the horizontal (left/right) and vertical (top/bottom) faces:
        * `horizontalRise = offsetX + halfWidthDiff`
        * `verticalRise = offsetY + halfHeightDiff`
    6.  Iterate through each `lenInches` in `TransitionSettings.LengthsInches`. For each length:
        * Convert `lenInches` to `lenFeet`. This is the **run**.
        * Calculate `horizontalAngle = arctan(horizontalRise / lenFeet)`.
        * Calculate `verticalAngle = arctan(verticalRise / lenFeet)`.
        * Convert both angles to degrees.
        * If both `horizontalAngle` and `verticalAngle` are less than or equal to `TransitionSettings.MaxAngleDeg`, **return `lenInches`**.
    7.  If the loop completes without finding a valid length, **return `null`**.

---
#### **`TransitionSettings` Class**

* This static class holds two public static fields:
    * `public static double MaxAngleDeg = 30.0;`
    * `public static List<int> LengthsInches = new() { 8, 12, 16, 24, 30, 36 };` (You can add more lengths as needed).

---
#### **`ConnectorUtils` Class**

* This static class holds two helper methods:
    1.  `public static Connector GetClosestUnusedConnector(Element element, XYZ point)`: Iterates through the element's `UnusedConnectors` and returns the one with the minimum `DistanceTo` the provided point.
    2.  `public static ConnectorManager GetConnectorManager(Element element)`: Safely gets the `ConnectorManager` from an element, checking if it's an `MEPCurve` or a `FamilyInstance` with an `MEPModel`.

---
#### **`SelectionFilters` Class**

* This static class contains two nested public classes that implement `ISelectionFilter`:
    1.  `NoInsulationFilter`: The `AllowElement` method should return `true` only for `Duct` elements that are not `InsulationLiningBase`.
    2.  `NoInsulationNoFamilyInstanceFilter`: The `AllowElement` method should return `true` only for `Duct` elements that are not `InsulationLiningBase` or `FamilyInstance`.
     */

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

                bool userAcknowledgedAngleOverride = false;
                //double? bestLengthFeet = FittingEvaluator.GetShortestValidLengthFeetByCornerOffsets(conn1, conn2);
                int? bestLengthInches = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

                if (bestLengthInches == null)
                {
                    TaskDialog td = new TaskDialog("Angle Constraint")
                    {
                        MainInstruction = "All transition types exceed 30° angle limit.",
                        MainContent = "Do you want to proceed anyway?",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };

                    if (td.Show() == TaskDialogResult.No)
                        return Result.Cancelled;

                    userAcknowledgedAngleOverride = true;
                }


                using (Transaction tx = new Transaction(doc, "Place ACCO Transition"))
                {
                    tx.Start();

                    FamilyInstance fittingInstance = doc.Create.NewTransitionFitting(conn1, conn2);
                    if (fittingInstance == null)
                    {
                        TaskDialog.Show("Error", "Failed to create transition fitting.");
                        tx.RollBack();
                        return Result.Failed;
                    }

                    if (!userAcknowledgedAngleOverride && bestLengthInches.HasValue)
                    {
                        // --- REFACTORED LOGIC ---

                        // 1. Get all symbol types from the fitting's family ONCE.
                        //    Using .ToList() executes the query and stores results in memory.
                        List<FamilySymbol> availableSymbols = new FilteredElementCollector(doc)
                            .OfClass(typeof(FamilySymbol))
                            .Cast<FamilySymbol>()
                            .Where(s => s.Family.Id == fittingInstance.Symbol.Family.Id)
                            .ToList();

                        // Convert the integer length to a string for searching. 12 -> "12"
                        string lengthStr = bestLengthInches.Value.ToString();

                        // 2. Search the list we already created. This is much faster.
                        FamilySymbol targetSymbol = availableSymbols
                            .FirstOrDefault(s => s.Name.Contains(lengthStr));

                        if (targetSymbol != null && targetSymbol.Id != fittingInstance.Symbol.Id)
                        {
                            fittingInstance.Symbol = targetSymbol;
                        }
                    }

                    tx.Commit();
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
        /// <summary>
        /// Calculates the shortest valid transition length by checking the slope of the four faces.
        /// This method matches how Revit calculates transition angles.
        /// </summary>
        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
        {
            if (conn1.Shape != ConnectorProfileType.Rectangular ||
                conn2.Shape != ConnectorProfileType.Rectangular)
            {
                TaskDialog.Show("Unsupported Type", "This function currently only supports rectangular duct connectors.");
                return null;
            }

            // --- Calculate the total offset for each of the four faces ---

            // The vector from the center of connector 1 to the center of connector 2
            XYZ centerOffset = conn2.Origin - conn1.Origin;

            // The change in size for each half of the duct
            double halfWidthDiff = System.Math.Abs(conn2.Width - conn1.Width) / 2.0;
            double halfHeightDiff = System.Math.Abs(conn2.Height - conn1.Height) / 2.0;

            // The offset of the duct centers, resolved along the connector's local X and Y axes
            double offsetX = System.Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisX));
            double offsetY = System.Math.Abs(centerOffset.DotProduct(conn1.CoordinateSystem.BasisY));

            // The "rise" for the horizontal (left/right) and vertical (top/bottom) faces
            double horizontalRise = offsetX + halfWidthDiff;
            double verticalRise = offsetY + halfHeightDiff;


            // --- Find the shortest length that satisfies the angle constraints ---

            foreach (int lenInches in TransitionSettings.LengthsInches)
            {
                double lenFeet = lenInches / 12.0;
                if (lenFeet <= 0) continue;

                // Calculate the angle for the horizontal and vertical faces
                double horizontalAngle = System.Math.Atan(horizontalRise / lenFeet) * (180.0 / System.Math.PI);
                double verticalAngle = System.Math.Atan(verticalRise / lenFeet) * (180.0 / System.Math.PI);

                // Check if both resulting angles are within the allowed maximum
                if (horizontalAngle <= TransitionSettings.MaxAngleDeg &&
                    verticalAngle <= TransitionSettings.MaxAngleDeg)
                {
                    // This is the shortest valid length
                    return lenInches;
                }
            }

            // No available length could satisfy the constraints
            return null;
        }
    }
    public static class FittingEvaluator3
    {
        /// <summary>
        /// Calculates the shortest valid transition length by checking the slope of all four corners.
        /// This is more accurate than checking only the center-point offsets.
        /// </summary>
        /// <param name="conn1">The first connector.</param>
        /// <param name="conn2">The second connector.</param>
        /// <returns>The shortest valid length in inches from the settings list, or null if none are valid.</returns>
        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
        {
            // First, check if both connectors are rectangular.
            if (conn1.Shape != ConnectorProfileType.Rectangular ||
                conn2.Shape != ConnectorProfileType.Rectangular)
            {
                TaskDialog.Show("Unsupported Type", "This function currently only supports rectangular duct connectors.");
                return null;
            }

            // 1. Get the world coordinates of the four corners for each connector.
            List<XYZ> corners1 = GetConnectorCorners(conn1);
            List<XYZ> corners2 = GetConnectorCorners(conn2);

            // 2. Iterate through available lengths to find the shortest one that works.
            foreach (int lenInches in TransitionSettings.LengthsInches)
            {
                double lenFeet = lenInches / 12.0;
                if (lenFeet <= 0) continue;

                bool allCornersAreValid = true;

                // 3. For the current length, check all four corner-to-corner slopes.
                for (int i = 0; i < 4; i++)
                {
                    XYZ p1 = corners1[i];
                    XYZ p2 = corners2[i];

                    double dx = System.Math.Abs(p2.X - p1.X);
                    double dy = System.Math.Abs(p2.Y - p1.Y);

                    // IMPORTANT: We do not use dz to calculate an angle. The fitting's length
                    // accounts for the separation along the primary axis.

                    double angleX = System.Math.Atan(dx / lenFeet) * (180.0 / System.Math.PI);
                    double angleY = System.Math.Atan(dy / lenFeet) * (180.0 / System.Math.PI);

                    // If ANY corner on the X or Y axis exceeds the max angle, this length is invalid.
                    // The check for angleZ has been removed.
                    if (angleX > TransitionSettings.MaxAngleDeg ||
                        angleY > TransitionSettings.MaxAngleDeg)
                    {
                        allCornersAreValid = false;
                        break; // No need to check other corners; this length has failed.
                    }
                }

                // 4. If all four corners passed the check, this is our shortest valid length.
                if (allCornersAreValid)
                {
                    return lenInches;
                }
            }

            // No available length was sufficient for all corners.
            return null;
        }

        /// <summary>
        /// Helper method to get the four corner points of a rectangular connector in world coordinates.
        /// </summary>
        /// <param name="conn">The rectangular connector.</param>
        /// <returns>A list of four XYZ points representing the corners.</returns>
        private static List<XYZ> GetConnectorCorners(Connector conn)
        {
            XYZ origin = conn.Origin;
            XYZ basisX = conn.CoordinateSystem.BasisX; // The "right" direction
            XYZ basisY = conn.CoordinateSystem.BasisY; // The "up" direction
            double width = conn.Width;
            double height = conn.Height;

            XYZ halfWidthVec = basisX * (width / 2.0);
            XYZ halfHeightVec = basisY * (height / 2.0);

            var corners = new List<XYZ>
        {
            origin + halfWidthVec + halfHeightVec, // Top-Right
            origin - halfWidthVec + halfHeightVec, // Top-Left
            origin - halfWidthVec - halfHeightVec, // Bottom-Left
            origin + halfWidthVec - halfHeightVec  // Bottom-Right
        };

            return corners;
        }
    }
    public static class FittingEvaluator2
    {
        /// <summary>
        /// Calculates the shortest valid transition length by checking the slope of all four corners.
        /// This is more accurate than checking only the center-point offsets.
        /// </summary>
        /// <param name="conn1">The first connector.</param>
        /// <param name="conn2">The second connector.</param>
        /// <returns>The shortest valid length in inches from the settings list, or null if none are valid.</returns>
        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
        {
            // First, check if both connectors are rectangular.
            if (conn1.Shape != ConnectorProfileType.Rectangular ||
                conn2.Shape != ConnectorProfileType.Rectangular)
            {
                // This logic is specifically for rectangular transitions.
                // You could add a fallback to a center-point method here if needed.
                TaskDialog.Show("Error", "This function only supports rectangular duct connectors.");
                return null;
            }

            // 1. Get the world coordinates of the four corners for each connector.
            List<XYZ> corners1 = GetConnectorCorners(conn1);
            List<XYZ> corners2 = GetConnectorCorners(conn2);

            // 2. Iterate through available lengths to find the shortest one that works.
            foreach (int lenInches in TransitionSettings.LengthsInches)
            {
                double lenFeet = lenInches / 12.0;
                if (lenFeet <= 0) continue;

                bool allCornersAreValid = true;

                // 3. For the current length, check all four corner-to-corner slopes.
                for (int i = 0; i < 4; i++)
                {
                    XYZ p1 = corners1[i];
                    XYZ p2 = corners2[i];

                    double dx = System.Math.Abs(p2.X - p1.X);
                    double dy = System.Math.Abs(p2.Y - p1.Y);
                    double dz = System.Math.Abs(p2.Z - p1.Z);

                    double angleX = System.Math.Atan(dx / lenFeet) * (180.0 / System.Math.PI);
                    double angleY = System.Math.Atan(dy / lenFeet) * (180.0 / System.Math.PI);
                    double angleZ = System.Math.Atan(dz / lenFeet) * (180.0 / System.Math.PI);

                    // If ANY corner at ANY axis exceeds the max angle, this length is invalid.
                    if (angleX > TransitionSettings.MaxAngleDeg ||
                        angleY > TransitionSettings.MaxAngleDeg ||
                        angleZ > TransitionSettings.MaxAngleDeg)
                    {
                        allCornersAreValid = false;
                        break; // No need to check other corners for this length; it's already failed.
                    }
                }

                // 4. If all four corners passed the check, this is our shortest valid length.
                if (allCornersAreValid)
                {
                    return lenInches;
                }
            }

            // No available length was sufficient for all corners.
            return null;
        }

        /// <summary>
        /// Helper method to get the four corner points of a rectangular connector in world coordinates.
        /// </summary>
        /// <param name="conn">The rectangular connector.</param>
        /// <returns>A list of four XYZ points representing the corners.</returns>
        private static List<XYZ> GetConnectorCorners(Connector conn)
        {
            // Get the connector's local coordinate system vectors and dimensions.
            XYZ origin = conn.Origin;
            XYZ basisX = conn.CoordinateSystem.BasisX; // The "right" direction
            XYZ basisY = conn.CoordinateSystem.BasisY; // The "up" direction
            double width = conn.Width;
            double height = conn.Height;

            // Calculate vectors representing half the width and half the height.
            XYZ halfWidthVec = basisX * (width / 2.0);
            XYZ halfHeightVec = basisY * (height / 2.0);

            // Calculate the four corners relative to the origin using the vectors.
            var corners = new List<XYZ>
        {
            origin + halfWidthVec + halfHeightVec, // Top-Right
            origin - halfWidthVec + halfHeightVec, // Top-Left
            origin - halfWidthVec - halfHeightVec, // Bottom-Left
            origin + halfWidthVec - halfHeightVec  // Bottom-Right
        };

            return corners;
        }
    }
    public static class FittingEvaluator1
    {
        // Renamed to reflect it returns inches
        public static int? GetShortestValidLengthInches(Connector conn1, Connector conn2)
        {
            XYZ b1 = conn1.Origin;
            XYZ b2 = conn2.Origin;

            double dx = Math.Abs(b2.X - b1.X);
            double dy = Math.Abs(b2.Y - b1.Y);
            double dz = Math.Abs(b2.Z - b1.Z);

            foreach (int lenInches in TransitionSettings.LengthsInches)
            {
                double lenFeet = lenInches / 12.0;

                // Added a check to prevent division by zero, just in case
                if (lenFeet == 0) continue;

                double angleX = Math.Atan(dx / lenFeet) * (180.0 / Math.PI);
                double angleY = Math.Atan(dy / lenFeet) * (180.0 / Math.PI);
                double angleZ = Math.Atan(dz / lenFeet) * (180.0 / Math.PI);

                if (angleX <= TransitionSettings.MaxAngleDeg &&
                    angleY <= TransitionSettings.MaxAngleDeg &&
                    angleZ <= TransitionSettings.MaxAngleDeg)
                {
                    // Return the valid length in INCHES
                    return lenInches;
                }
            }
            return null; // No valid length found
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
}




// #### needs updating the promts to only one
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

//                double? bestLengthFeet = FittingEvaluator.GetShortestValidLengthFeet(conn1.Origin, conn2.Origin);

//                if (bestLengthFeet == null)
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = "All transition types exceed 30° angle limit.",
//                        MainContent = "Do you want to proceed anyway?",
//                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                        DefaultButton = TaskDialogResult.No
//                    };

//                    if (td.Show() == TaskDialogResult.No)
//                        return Result.Cancelled;
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

//                    if (!FittingEvaluator.TryFindBestFitting(doc, fittingInstance, out FamilySymbol bestSymbol))
//                    {
//                        TaskDialog td = new TaskDialog("Angle Constraint")
//                        {
//                            MainInstruction = "All transition types exceed 30° angle limit.",
//                            MainContent = "Do you want to keep the default fitting?",
//                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                            DefaultButton = TaskDialogResult.No
//                        };

//                        if (td.Show() == TaskDialogResult.No)
//                        {
//                            doc.Delete(fittingInstance.Id);
//                            tx.Commit();
//                            return Result.Cancelled;
//                        }
//                    }
//                    else
//                    {
//                        fittingInstance.Symbol = bestSymbol;
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
//        public static bool TryFindBestFitting(Document doc, FamilyInstance? fittingInstance, out FamilySymbol bestSymbol)
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
//                fittingInstance.Symbol = symbol;

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

//        public static double? GetShortestValidLengthFeet(XYZ origin1, XYZ origin2)
//        {
//            double deltaX = origin2.X - origin1.X;
//            double deltaY = origin2.Y - origin1.Y;
//            double deltaZ = origin2.Z - origin1.Z;

//            double xyOffset = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
//            double zOffset = Math.Abs(deltaZ);

//            foreach (int lenInches in TransitionSettings.LengthsInches)
//            {
//                double lenFeet = lenInches / 12.0;
//                double angleXY = Math.Atan(xyOffset / lenFeet) * (180.0 / Math.PI);
//                double angleZ = Math.Atan(zOffset / lenFeet) * (180.0 / Math.PI);

//                if (angleXY <= TransitionSettings.MaxAngleDeg && angleZ <= TransitionSettings.MaxAngleDeg)
//                {
//                    return lenFeet;
//                }
//            }

//            return null;
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

//#########################################################



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

//                double? bestLengthFeet = FittingEvaluator.GetShortestValidLengthFeet(conn1.Origin, conn2.Origin);
//                if (bestLengthFeet == null)
//                {
//                    TaskDialog td = new TaskDialog("Angle Constraint")
//                    {
//                        MainInstruction = "All transition types exceed 30° angle limit.",
//                        MainContent = "Please adjust duct layout or consult detailing.",
//                        CommonButtons = TaskDialogCommonButtons.Ok
//                    };
//                    td.Show();
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

//        public static double? GetShortestValidLengthFeet(XYZ origin1, XYZ origin2)
//        {
//            double deltaX = origin2.X - origin1.X;
//            double deltaY = origin2.Y - origin1.Y;
//            double deltaZ = origin2.Z - origin1.Z;

//            double xyOffset = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
//            double zOffset = Math.Abs(deltaZ);

//            foreach (int lenInches in TransitionSettings.LengthsInches)
//            {
//                double lenFeet = lenInches / 12.0;
//                double angleXY = Math.Atan(xyOffset / lenFeet) * (180.0 / Math.PI);
//                double angleZ = Math.Atan(zOffset / lenFeet) * (180.0 / Math.PI);

//                if (angleXY <= TransitionSettings.MaxAngleDeg && angleZ <= TransitionSettings.MaxAngleDeg)
//                {
//                    return lenFeet;
//                }
//            }

//            return null;
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