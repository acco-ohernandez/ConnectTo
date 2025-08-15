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
                    IList<Reference> pickedRefs = uiDoc.Selection.PickObjects(ObjectType.Element, new TransitionFittingSelectionFilter(), "Select duct transition fittings to update");
                    foreach (Reference reference in pickedRefs)
                    {
                        Element elem = doc.GetElement(reference);
                        if (IsValidTransitionFitting(elem))
                        {
                            transitionFittings.Add((FamilyInstance)elem);
                        }
                    }
                }

                if (transitionFittings.Count == 0)
                {
                    TaskDialog.Show("Info", "No duct transition fittings selected.");
                    return Result.Cancelled;
                }

                List<(FamilyInstance fitting, FamilySymbol targetSymbol)> compliantFittings = new();
                List<FamilyInstance> nonCompliantFittings = new();

                // Evaluate fittings BEFORE starting the transaction
                foreach (FamilyInstance fitting in transitionFittings)
                {
                    ConnectorManager cm = fitting.MEPModel?.ConnectorManager;
                    if (cm == null || cm.Connectors.Size != 2)
                    {
                        nonCompliantFittings.Add(fitting);
                        continue;
                    }

                    var connectors = cm.Connectors.Cast<Connector>().ToList();
                    Connector conn1 = connectors[0];
                    Connector conn2 = connectors[1];

                    int? bestLength = FittingEvaluator.GetShortestValidLengthInches(conn1, conn2);

                    if (bestLength == null)
                    {
                        nonCompliantFittings.Add(fitting);
                        continue;
                    }

                    string lengthStr = bestLength.Value.ToString();
                    Family family = fitting.Symbol.Family;

                    FamilySymbol targetSymbol = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilySymbol))
                        .Cast<FamilySymbol>()
                        .Where(s => s.Family.Id == family.Id && s.Name.Contains(lengthStr))
                        .FirstOrDefault();

                    if (targetSymbol == null || targetSymbol.Id == fitting.Symbol.Id)
                    {
                        continue;
                    }

                    compliantFittings.Add((fitting, targetSymbol));
                }

                using (Transaction tx = new Transaction(doc, "Update Transition Fittings"))
                {
                    tx.Start();

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
                            string nonCompliantIds = string.Join("\n", nonCompliantFittings.Select(f => f.Id.ToString()));
                            TaskDialog.Show("Non-Compliant IDs", $"The following fitting IDs were not updated due to exceeding angle constraints:\n{nonCompliantIds}");
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
                "This button checks that all the transitions in a duct system meet ACCO Angle Constrains."
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