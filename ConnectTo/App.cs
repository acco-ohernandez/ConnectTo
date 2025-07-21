namespace ConnectTo
{
    internal class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            // 1. Create ribbon tab
            string tabName = "ConTech";
            try
            {
                app.CreateRibbonTab(tabName);
            }
            catch (Exception)
            {
                Debug.Print("Tab already exists.");
            }

            //// 2. Create ribbon panel 
            RibbonPanel panel = Utils.Common.CreateRibbonPanel(app, tabName, "Dev");

            //// 3. Create button data instances
            //// 4. Create buttons
            PushButtonData btnData1 = Cmd_ConnectTo.GetButtonData();
            PushButton myButton1 = panel.AddItem(btnData1) as PushButton;

            PushButtonData btnData2 = Cmd_Parallel.GetButtonData();
            PushButton myButton2 = panel.AddItem(btnData2) as PushButton;

            PushButtonData btnData3 = TestAllRevitTaskDialongs.GetButtonData();
            PushButton myButton3 = panel.AddItem(btnData3) as PushButton;

            //PushButtonData btnTemplate = Cmd_Template.GetButtonData();
            //PushButton myButton = panel.AddItem(btnData2) as PushButton;

            // NOTE:
            // To create a new tool, copy lines 35 and 39 and rename the variables to "btnData3" and "myButton3". 
            // Change the name of the tool in the arguments of line 

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            return Result.Succeeded;
        }
    }

}
