using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConnectTo
{
    [Transaction(TransactionMode.Manual)]
    public class TestAllRevitTaskDialongs : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // this is a variable for the Revit application
            UIApplication uiapp = commandData.Application;

            // this is a variable for the current Revit model
            Document doc = uiapp.ActiveUIDocument.Document;
            // This will show all the different Task Dialogs from the RevitTaskDialog.cs wrapper class
            try
            {
                // Example 1: Basic dialog with title and message
                new RevitTaskDialogBuilder("Basic Dialog", "First test.")
                    .WithContent("This is a basic dialog with a title and a message.")
                    .Show();

                // Example 2: Dialog with Information icon
                new RevitTaskDialogBuilder("Info", "Operation Successful")
                    .WithContent("The operation was completed without any errors.")
                    .WithIcon(RevitTaskDialog.DialogIconType.Information)
                    .Show();

                // Example 3: Warning dialog with no command links
                new RevitTaskDialogBuilder("Warning", "Unusual Behavior Detected")
                    .WithContent("Please check your input values.")
                    .WithIcon(RevitTaskDialog.DialogIconType.Warning)
                    .Show();

                // Example 4: Error dialog
                new RevitTaskDialogBuilder("Critical Error", "An exception has occurred")
                    .WithContent("A severe error has caused the process to stop.")
                    .WithIcon(RevitTaskDialog.DialogIconType.Error)
                    .Show();

                // Example 5: Shield (security) dialog
                new RevitTaskDialogBuilder("Permission Required", "Administrator Access Needed")
                    .WithContent("You must be an administrator to perform this action.")
                    .WithIcon(RevitTaskDialog.DialogIconType.Shield)
                    .Show();

                // Example 6: Dialog with command links for user choices
                new RevitTaskDialogBuilder("Save Changes", "Would you like to save your changes?")
                    .WithContent("Unsaved changes will be lost if you don't save.")
                    .WithIcon(RevitTaskDialog.DialogIconType.Warning)
                    .AddCommandLink("Save", () =>
                    {
                        new RevitTaskDialogBuilder("Saved", "Your changes have been saved.")
                            .WithIcon(RevitTaskDialog.DialogIconType.Information)
                            .Show();
                    })
                    .AddCommandLink("Don't Save", () =>
                    {
                        new RevitTaskDialogBuilder("Not Saved", "Your changes were discarded.")
                            .WithIcon(RevitTaskDialog.DialogIconType.Warning)
                            .Show();
                    })
                    .Show();

                // Example 7: Simulate and show an exception dialog
                try
                {
                    throw new InvalidOperationException("Simulated exception for testing", new ArgumentNullException("parameterName"));
                }
                catch (Exception ex)
                {
                    RevitTaskDialog.ShowExceptionDialog("Exception Test", ex);
                }
            }
            catch (Exception ex)
            {
                RevitTaskDialog.ShowExceptionDialog("Unhandled Error", ex);
            }


            return Result.Succeeded;
        }
        internal static PushButtonData GetButtonData()
        {
            // use this method to define the properties for this command in the Revit ribbon
            string buttonInternalName = "btnTemplate";
            string buttonTitle = "TaskDioalogs";

            Utils.ButtonDataClass myButtonData1 = new Utils.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.Green_32,
                Properties.Resources.Green_16,
                "This button will show all the different Task Dialogs from the RevitTaskDialog.cs Wrapper class");

            return myButtonData1.Data;
        }
    }

}
