using System;

namespace Sona_Clipboard.Services.Interfaces
{
    /// <summary>
    /// Service for subclassing a window to intercept messages (e.g., WM_HOTKEY)
    /// </summary>
    public interface IWindowSubclassService : IDisposable
    {
        /// <summary>
        /// Installs the subclass procedure for the specified window handle.
        /// </summary>
        /// <param name="hWnd">The window handle to subclass.</param>
        void Hook(IntPtr hWnd);

        /// <summary>
        /// Removes the subclass procedure.
        /// </summary>
        void Unhook();
    }
}
