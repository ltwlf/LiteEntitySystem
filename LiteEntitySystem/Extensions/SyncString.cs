using System;
using System.Text;

namespace LiteEntitySystem.Extensions
{
    /// <summary>
    /// A SyncableField that holds a string. On the server side, setting Value
    /// replicates the new string data to clients. On the client side, when
    /// updated from the server, it fires OnValueChanged(string).
    /// </summary>
    public class SyncString : SyncableField
    {
        private static readonly UTF8Encoding Encoding = new(false, true);

        // The actual string data
        private string _string = string.Empty;

        // A cached buffer for UTF-8 encoding
        private byte[] _stringData;
        private int _size;

        // Cached remote call for "SetNewString"
        private static RemoteCallSpan<byte> _setStringClientCall;

        /// <summary>
        /// This event is fired on the client side whenever the server
        /// actually updates the string to a new value.
        /// </summary>
        public event Action<string> OnValueChanged;

        /// <summary>
        /// The user-facing property. If we are on the server and set it,
        /// we replicate that new string to all clients.
        /// </summary>
        public string Value
        {
            get => _string;
            set
            {
                if (_string == value)
                    return; // no change, do nothing

                _string = value;

                // Convert to UTF-8 bytes
                Utils.ResizeOrCreate(ref _stringData, Encoding.GetMaxByteCount(_string.Length));
                _size = Encoding.GetBytes(_string, 0, _string.Length, _stringData, 0);

                // If server, replicate out
                if (IsServer)
                {
                    ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
                }
            }
        }

        /// <summary>
        /// So you can do "string s = mySyncString;"
        /// </summary>
        public static implicit operator string(SyncString s)
        {
            return s.Value;
        }

        /// <summary>
        /// Registers a client action (SetNewString) so that
        /// when the server calls _setStringClientCall, the client runs SetNewString.
        /// </summary>
        protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
        {
            r.CreateClientAction(this, SetNewString, ref _setStringClientCall);
        }

        /// <summary>
        /// Called on the server if the library wants to sync the
        /// current state to a new client or do a full baseline sync, etc.
        /// We replicate the current string data (if any).
        /// </summary>
        protected internal override void OnSyncRequested()
        {
            if (IsServer && _size > 0)
            {
                ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
            }
        }

        /// <summary>
        /// This method is invoked on the client when the server updates the string.
        /// We decode the new bytes, see if it's changed, and if so, fire OnValueChanged(string).
        /// We also call RaiseClientValueChanged() to satisfy the base SyncableField.
        /// </summary>
        private void SetNewString(ReadOnlySpan<byte> data)
        {
            string newVal = Encoding.GetString(data);
            if (_string != newVal)
            {
                _string = newVal;

                // 1) Fire the typed event for the client
                OnValueChanged?.Invoke(_string);
            }
        }

        public override string ToString() => _string;
    }
}
