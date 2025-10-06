using System;
using System.Windows.Forms;

namespace RsiWatcherGUI
{
    public static class ControlExtensions
    {
        public static void InvokeIfRequired(this Control c, Action action)
        {
            if (c == null) return;
            if (c.InvokeRequired) c.Invoke(action); else action();
        }
    }
}
