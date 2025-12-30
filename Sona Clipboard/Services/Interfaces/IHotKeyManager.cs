using System;

namespace Sona_Clipboard.Services.Interfaces
{
    public interface IHotKeyManager
    {
        void RaiseHotKeyPressed(int hotkeyId);
    }
}
