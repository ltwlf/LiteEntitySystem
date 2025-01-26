using System;
using System.Text;
using LiteEntitySystem;

public class SyncString : SyncableField
{
    private static readonly UTF8Encoding Encoding = new(false, true);
    private byte[] _stringData;
    private string _string;
    private int _size;

    // A cached RemoteCall handle for "client actions" 
    // that set the string on the client side.
    private static RemoteCallSpan<byte> _setStringClientCall;

    /// <summary>
    /// The user-facing property for reading/writing the string.
    /// Setting this property on the server replicates the change.
    /// Setting on the client is local only (unless you handle client->server separately).
    /// </summary>
    public string Value
    {
        get => _string;
        set
        {
            if (_string == value)
                return;

            _string = value;

            // Allocate or resize the byte array if needed.
            Utils.ResizeOrCreate(ref _stringData, Encoding.GetMaxByteCount(_string.Length));

            // Encode to UTF8.
            _size = Encoding.GetBytes(_string, 0, _string.Length, _stringData, 0);

            // If we are on the server, replicate out to all clients.
            if (IsServer)
            {
                ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
            }
        }
    }

    /// <summary>
    /// Called once to register the "OnServerUpdate" method as a client action.
    /// That means when the server executes _setStringClientCall, 
    /// the client will run OnServerUpdate(ReadOnlySpan<byte>).
    /// </summary>
    protected internal override void RegisterRPC(ref SyncableRPCRegistrator r)
    {
        // We want a "client action" that receives a ReadOnlySpan<byte> 
        // containing the updated string data from the server.
        r.CreateClientAction(this, OnServerUpdate, ref _setStringClientCall);
    }

    /// <summary>
    /// This method is invoked on the client side when the server
    /// sends the "set string" RPC. We decode the bytes, check if changed,
    /// then call RaiseClientValueChanged() if so.
    /// </summary>
    private void OnServerUpdate(ReadOnlySpan<byte> data)
    {
        string newVal = Encoding.GetString(data);
        if (_string != newVal)
        {
            _string = newVal;
            // We changed the field => must call this or the base SyncableField 
            // will throw in AfterReadRPC().
            RaiseClientValueChanged();
        }
    }

    /// <summary>
    /// If the library calls "OnSyncRequested()" on the server,
    /// we do the initial replication so clients get the current string.
    /// </summary>
    protected internal override void OnSyncRequested()
    {
        if (IsServer && _stringData != null && _size > 0)
        {
            // Send the current string data to clients.
            ExecuteRPC(_setStringClientCall, new ReadOnlySpan<byte>(_stringData, 0, _size));
        }
    }
    public static implicit operator string(SyncString s)
    {
        return s.Value;
    }
}