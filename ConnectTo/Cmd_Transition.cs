using System;
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
    /// <summary>
    /// Implements an interactive Revit external command for creating transition fittings between 
    /// two MEP elements (e.g., ducts, pipes, cable trays) by selecting their unused connectors in the model.
    /// 
    /// The command operates in a loop, repeatedly prompting the user to:
    /// 1. Select the first element (movable connector side).
    /// 2. Select the second element (static connector side).
    /// 
    /// For each pair, it identifies the closest unused connector to the picked points, verifies that both 
    /// connectors belong to the same domain, and attempts to create a transition fitting between them 
    /// using Revit's API. If the connection is invalid or fails, the user is notified with an error message.
    /// 
    /// Selection filters are applied to ensure only valid MEP elements (excluding insulation, lining, and 
    /// optionally family instances) can be chosen. The class also includes utility methods for connector 
    /// retrieval and management, and provides ribbon button data for integration into the Revit UI.
    /// 
    /// The process continues until the user cancels the selection operation.
    /// </summary>


    [Transaction(TransactionMode.Manual)]
    public class Cmd_Transition : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // Keep prompting until user cancels
                while (true)
                {
                    if (!CreateTransition(uidoc, doc))
                        break; // Exit loop if user cancels or fails
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                // Graceful exit when user presses ESC
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"An unexpected error occurred: {ex.Message}");
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private bool CreateTransition(UIDocument uidoc, Document doc)
        {
            try
            {
                // STEP 1: Pick first element (movable connector side)
                Reference pick1 = uidoc.Selection.PickObject(ObjectType.Element, new NoInsulationNoFamilyInstanceFilter(),
                    "Pick element 1 (the connector moves depending on transition length)");
                Element elem1 = doc.GetElement(pick1);
                XYZ xyz1 = pick1.GlobalPoint;

                // STEP 2: Pick second element (static connector side)
                Reference pick2 = uidoc.Selection.PickObject(ObjectType.Element, new NoInsulationFilter(),
                    "Pick element 2 (static)");
                Element elem2 = doc.GetElement(pick2);
                XYZ xyz2 = pick2.GlobalPoint;

                // STEP 3: Get closest unused connectors
                Connector conn1 = GetClosestUnusedConnector(elem1, xyz1);
                Connector conn2 = GetClosestUnusedConnector(elem2, xyz2);

                // STEP 4: Validate connectors
                if (conn1 == null || conn2 == null)
                {
                    TaskDialog.Show("Error", "One of the selected elements has no unused connector.");
                    return true;
                }

                if (conn1.Domain != conn2.Domain)
                {
                    TaskDialog.Show("Domain Error", "You picked connectors of different domains. Please retry.");
                    return true;
                }

                // STEP 5: Create the transition
                using (Transaction tx = new Transaction(doc, "Create Transition"))
                {
                    tx.Start();
                    try
                    {
                        doc.Create.NewTransitionFitting(conn1, conn2);
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                        TaskDialog.Show("Unable to Connect",
                            "Make sure you click near connectors you want to connect.");
                    }
                    tx.Commit();
                }

                return true; // Continue loop
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return false; // Exit loop on ESC
            }
        }

        private Connector GetClosestUnusedConnector(Element element, XYZ point)
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

        private ConnectorManager GetConnectorManager(Element element)
        {
            if (element is MEPCurve mepCurve)
                return mepCurve.ConnectorManager;
            if (element is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;
            return null;
        }

        // --- Selection Filters ---
        private class NoInsulationFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is InsulationLiningBase) return false;
                return GetConnectorManagerStatic(elem) != null;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private class NoInsulationNoFamilyInstanceFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                if (elem is InsulationLiningBase || elem is FamilyInstance) return false;
                return GetConnectorManagerStatic(elem) != null;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        private static ConnectorManager GetConnectorManagerStatic(Element element)
        {
            if (element is MEPCurve mepCurve)
                return mepCurve.ConnectorManager;
            if (element is FamilyInstance fi && fi.MEPModel != null)
                return fi.MEPModel.ConnectorManager;
            return null;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnTransition";
            string buttonTitle = "Transition";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Yellow_32,
                Properties.Resources.Yellow_16,
                "This button creates an MEP transition fitting between two open unused connectors."
            );

            return myButtonData1.Data;
        }
    }
}
