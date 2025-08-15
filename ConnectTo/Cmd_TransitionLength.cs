using System;
using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.Linq;
using System.Linq;
using System.Reflection;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ConnectTo
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_TransitionLength : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            List<FamilyInstance> transitionFittings = new();

            try
            {
                // Try to get preselected elements first
                ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();

                if (selectedIds.Count > 0)
                {
                    foreach (ElementId id in selectedIds)
                    {
                        Element elem = doc.GetElement(id);
                        if (IsValidTransitionFitting(elem))
                        {
                            transitionFittings.Add((FamilyInstance)elem);
                        }
                    }
                }

                // If no valid preselection, prompt user to select transitions manually
                if (transitionFittings.Count == 0)
                {
                    // Use a selection filter to allow only duct transition fittings
                    IList<Reference> pickedRefs = uiDoc.Selection.PickObjects(ObjectType.Element, new TransitionFittingSelectionFilter(), "Select duct transition fittings to update");
                    foreach (Reference reference in pickedRefs)
                    {
                        Element elem = doc.GetElement(reference);
                        // Chect that the element is a family instance, has a valid MEP model with connectors and is of category: "duct fitting"
                        if (IsValidTransitionFitting(elem))
                        {
                            transitionFittings.Add((FamilyInstance)elem);
                        }
                    }
                }
                // if the user cliked on finish without selecting any fittings, the transitionFittings list will still be empty
                if (transitionFittings.Count == 0)
                {
                    TaskDialog.Show("Info", "No duct transition fittings selected.");
                    return Result.Cancelled;
                }

                // Prepare lists for compliant and non-compliant fittings
                List<(FamilyInstance fitting, FamilySymbol targetSymbol)> compliantFittings = new();
                List<FamilyInstance> nonCompliantFittings = new();

                // Evaluate fittings BEFORE starting the transaction
                foreach (FamilyInstance fitting in transitionFittings)
                {
                    ConnectorManager connMgr = fitting.MEPModel?.ConnectorManager;
                    // if the connector manager is null or does not have exactly two connectors, mark as non-compliant and continue to the next fitting
                    if (connMgr == null || connMgr.Connectors.Size != 2)
                    {
                        nonCompliantFittings.Add(fitting);
                        continue;
                    }

                    // Get the connectors from the connector manager of the fitting
                    var connectors = connMgr.Connectors.Cast<Connector>().ToList();
                    Connector conn1 = connectors[0];
                    Connector conn2 = connectors[1];

                    // Get the sortest valid transition length in inches using the FittingEvaluator class or null if no valid length is found
                    int? bestLength = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

                    if (bestLength == null)
                    {
                        // If no valid length is found, mark as non-compliant
                        nonCompliantFittings.Add(fitting);
                        continue;
                    }

                    string lengthStr = bestLength.Value.ToString();

                    // Get the family of the fitting/transition
                    Family family = fitting.Symbol.Family;

                    // Find the target FamilySymbol that matches the family and contains the lengthStr in its name
                    FamilySymbol targetSymbol = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(s => s.Family.Id == family.Id && s.Name.Contains(lengthStr))
                        .FirstOrDefault();

                    // If no target symbol is found or if the target symbol is the same as the current fitting's symbol that means the fitting is already compliant, so we can skip it
                    if (targetSymbol == null || targetSymbol.Id == fitting.Symbol.Id)
                    {
                        continue;
                    }
                    // add the fitting and its target symbol to the compliant fittings list
                    compliantFittings.Add((fitting, targetSymbol));
                }

                using (Transaction tx = new Transaction(doc, "Update Transition Fittings"))
                {
                    tx.Start();
                    // loop through compliant fittings and update their symbols/type to the lenght that is compliant with the ACCO angle 30 degrees or less constraints
                    foreach (var (fitting, targetSymbol) in compliantFittings)
                    {

                        fitting.Symbol = targetSymbol;
                    }

                    if (nonCompliantFittings.Count > 0)
                    {
                        TaskDialog td = new TaskDialog("Non-Compliant Fittings")
                        {
                            MainInstruction = $"{nonCompliantFittings.Count} transition fittings exceed allowable angles.",
                            MainContent = "Do you want to proceed anyway?",
                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                            DefaultButton = TaskDialogResult.No
                        };

                        if (td.Show() == TaskDialogResult.No)
                        {
                            tx.RollBack();
                            return Result.Cancelled;
                        }
                        else
                        {
                            //string nonCompliantIds = string.Join("\n", nonCompliantFittings.Select(f => f.Id.ToString()));
                            //TaskDialog.Show("Non-Compliant IDs", $"The following fitting IDs were not updated due to exceeding angle constraints:\n{nonCompliantIds}");
                            // select all the non-compliant fittings in the UI
                            uiDoc.Selection.SetElementIds(nonCompliantFittings.Select(f => f.Id).ToList());
                            // tell the user the selected transitions are not compliant.
                            TaskDialog.Show("Ingo", "The non-compliant fittings have been left selected.");
                        }
                    }

                    tx.Commit();
                }

                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
                return Result.Failed;
            }
        }

        private bool IsValidTransitionFitting(Element elem)
        {
            return elem is FamilyInstance fi &&
                   fi.MEPModel?.ConnectorManager != null &&
                   fi.Symbol.Family.FamilyCategory.Name == "Duct Fittings";
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnTransitionLength";
            string buttonTitle = "Transition Length";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Blue_32,
                Properties.Resources.Blue_16,
                "This button checks that all the transitions in a duct system meet ACCO 30° or less Angle Constrains."
            );

            return myButtonData1.Data;
        }
    }

    public class TransitionFittingSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
            {
                return fi.Symbol.Family.FamilyCategory.Name == "Duct Fittings";
            }
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}



#region Prvious working version which has been broken because the Cmd_AccoTransition.cs has changed. 
//namespace ConnectTo
//{
//    [Transaction(TransactionMode.Manual)]
//    public class Cmd_TransitionLength : IExternalCommand
//    {
//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            UIApplication uiApp = commandData.Application;
//            UIDocument uiDoc = uiApp.ActiveUIDocument;
//            Document doc = uiDoc.Document;

//            List<FamilyInstance> transitionFittings = new();

//            try
//            {
//                // Try to get preselected elements first
//                ICollection<ElementId> selectedIds = uiDoc.Selection.GetElementIds();

//                if (selectedIds.Count > 0)
//                {
//                    foreach (ElementId id in selectedIds)
//                    {
//                        Element elem = doc.GetElement(id);
//                        if (IsValidTransitionFitting(elem))
//                        {
//                            transitionFittings.Add((FamilyInstance)elem);
//                        }
//                    }
//                }

//                // If no valid preselection, prompt user to select transitions manually
//                if (transitionFittings.Count == 0)
//                {
//                    IList<Reference> pickedRefs = uiDoc.Selection.PickObjects(ObjectType.Element, new TransitionFittingSelectionFilter(), "Select duct transition fittings to update");
//                    foreach (Reference reference in pickedRefs)
//                    {
//                        Element elem = doc.GetElement(reference);
//                        if (IsValidTransitionFitting(elem))
//                        {
//                            transitionFittings.Add((FamilyInstance)elem);
//                        }
//                    }
//                }

//                if (transitionFittings.Count == 0)
//                {
//                    TaskDialog.Show("Info", "No duct transition fittings selected.");
//                    return Result.Cancelled;
//                }

//                List<(FamilyInstance fitting, FamilySymbol bestSymbol)> compliantFittings = new();
//                List<FamilyInstance> nonCompliantFittings = new();

//                // Evaluate all fittings BEFORE starting the transaction
//                foreach (FamilyInstance fitting in transitionFittings)
//                {
//                    //if (FittingEvaluator.TryFindBestFitting(doc, fitting, out FamilySymbol bestSymbol))
//                    if (FittingEvaluator.TryFindBestFitting(doc, fitting, out FamilySymbol? bestSymbol) && bestSymbol != null)
//                    {
//                        compliantFittings.Add((fitting, bestSymbol));
//                    }
//                    else
//                    {
//                        nonCompliantFittings.Add(fitting);
//                    }
//                }

//                using (Transaction tx = new Transaction(doc, "Update Transition Fittings"))
//                {
//                    tx.Start();

//                    foreach (var (fitting, bestSymbol) in compliantFittings)
//                    {
//                        fitting.Symbol = bestSymbol;
//                    }

//                    if (nonCompliantFittings.Count > 0)
//                    {
//                        TaskDialog td = new TaskDialog("Non-Compliant Fittings")
//                        {
//                            MainInstruction = $"{nonCompliantFittings.Count} transition fittings exceed allowable angles.",
//                            MainContent = "Do you want to proceed anyway?",
//                            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
//                            DefaultButton = TaskDialogResult.No
//                        };

//                        if (td.Show() == TaskDialogResult.No)
//                        {
//                            tx.RollBack();
//                            return Result.Cancelled;
//                        }
//                        else
//                        {
//                            string nonCompliantIds = string.Join("\n", nonCompliantFittings.Select(f => f.Id.ToString()));
//                            TaskDialog.Show("Non-Compliant IDs", $"The following fitting IDs were not updated due to exceeding angle constraints:\n{nonCompliantIds}");
//                        }
//                    }

//                    tx.Commit();
//                }

//                return Result.Succeeded;
//            }
//            catch (OperationCanceledException)
//            {
//                return Result.Cancelled;
//            }
//            catch (Exception ex)
//            {
//                TaskDialog.Show("Error", $"Unexpected error: {ex.Message}");
//                return Result.Failed;
//            }
//        }

//        private bool IsValidTransitionFitting(Element elem)
//        {
//            return elem is FamilyInstance fi &&
//                   fi.MEPModel?.ConnectorManager != null &&
//                   fi.Symbol.Family.FamilyCategory.Name == "Duct Fittings";
//        }

//        internal static PushButtonData GetButtonData()
//        {
//            string buttonInternalName = "btnTransitionLength";
//            string buttonTitle = "Transition Length";

//            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
//                buttonInternalName,
//                buttonTitle,
//                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
//                Properties.Resources.Blue_32,
//                Properties.Resources.Blue_16,
//                "This button checks that all the transitions in a duct system meet ACCO Angle Constrains."
//            );

//            return myButtonData1.Data;
//        }
//    }

//    public class TransitionFittingSelectionFilter : ISelectionFilter
//    {
//        public bool AllowElement(Element elem)
//        {
//            if (elem is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)
//            {
//                return fi.Symbol.Family.FamilyCategory.Name == "Duct Fittings";
//            }
//            return false;
//        }

//        public bool AllowReference(Reference reference, XYZ position) => true;
//    }
//}

#endregion