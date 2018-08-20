using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace Digi
{
    public static class Log // Slim Log v1.1
    {
        public static void Info(string message, bool notify = false, int notifyTime = 5000)
        {
            MyLog.Default.WriteLine(message);
            MyLog.Default.Flush();

            if(notify)
                MyAPIGateway.Utilities?.ShowNotification($"[DEBUG] {message}", notifyTime, MyFontEnum.Green);
        }

        public static void Error(Exception e, bool notify = true, int notifyTime = 5000)
        {
            MyLog.Default.WriteLine(e);

            if(notify)
                MyAPIGateway.Utilities?.ShowNotification($"[ERROR] {e.Message}", notifyTime, MyFontEnum.Red);
        }
    }
}