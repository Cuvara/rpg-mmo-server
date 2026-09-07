using GameServer.Net;

namespace GameServer.Server;

/// <summary>
/// Atomic capacity admission for the join handshake.
///
/// <para><b>What it replaces.</b> The handshake used to compare
/// <c>ConnectionManager.Count</c> against <c>GAMESERVER_CAPACITY</c>, then <i>await</i> a
/// player-store load, then <c>Add</c> the connection. Every join that arrived during that
/// await observed the same free slot, so N concurrent joins against one remaining slot
/// admitted N players — the check and the add were separated by an I/O round trip and
/// nothing held the slot in between (workspace audit, "make capacity admission atomic").</para>
///
/// <para><b>What it is now.</b> A slot is <i>reserved</i> under one lock before anything is
/// awaited, and <i>committed</i> under the same lock when the connection is registered.
/// Occupancy is the number of distinct users that are connected or hold a slot-consuming
/// reservation; the reservation is released on every failure path between reserve and
/// commit, so an aborted join never keeps a slot.</para>
///
/// <para><b>Replacement, not addition.</b> A user who already holds a live connection here
/// (a half-dead socket the heartbeat has not noticed yet — the fast-rejoin case, #229)
/// is <i>replacing</i> that connection, so the reservation costs no slot: one user cannot
/// occupy two. A user inside the reconnect hold window is <b>not</b> an occupant — the hold
/// keeps their entity, not their slot — so their rejoin is admitted like any other join
/// and takes exactly one slot. That keeps this count equal to the <c>players_online</c>
/// the registry publishes, which is what the gateway steers on.</para>
/// </summary>
public sealed class AdmissionController
{
    private readonly ConnectionManager _connections;
    private readonly int _capacity;
    private readonly object _lock = new();

    /// <summary>
    /// Reservations not yet committed or released, by user id. The value records whether
    /// the reservation was taken as a replacement of a live connection (true), in which
    /// case it does not count towards occupancy.
    /// </summary>
    private readonly Dictionary<string, bool> _reserved = new(StringComparer.Ordinal);

    /// <summary>Reservations in <see cref="_reserved"/> whose value is false.</summary>
    private int _slotReservations;

    public AdmissionController(ConnectionManager connections, int capacity)
    {
        _connections = connections;
        _capacity = capacity;
    }

    /// <summary>The configured admission limit.</summary>
    public int Capacity => _capacity;

    /// <summary>Reservations taken and not yet committed or released. Diagnostics and tests.</summary>
    public int PendingReservations
    {
        get { lock (_lock) return _reserved.Count; }
    }

    /// <summary>
    /// Distinct users that are connected or hold a slot-consuming reservation — the number
    /// the capacity check compares against <see cref="Capacity"/>.
    /// </summary>
    public int Occupancy
    {
        get { lock (_lock) return OccupancyLocked(); }
    }

    private int OccupancyLocked() => _connections.Count + _slotReservations;

    /// <summary>
    /// Try to take a slot for <paramref name="userId"/>. Returns false when the server is
    /// full. A successful reservation must be followed by exactly one <see cref="Commit"/>
    /// or <see cref="Release"/> for the same user.
    /// </summary>
    /// <param name="occupancy">The occupancy observed, for the rejection log line.</param>
    public bool TryReserve(string userId, out int occupancy)
    {
        lock (_lock)
        {
            occupancy = OccupancyLocked();

            // Two joins for one user racing each other: the second replaces the first,
            // exactly as ConnectionManager.Add decides between their connections. The
            // slot (if any) is already held by the first reservation.
            if (_reserved.ContainsKey(userId))
            {
                return true;
            }

            if (_connections.Get(userId) is not null)
            {
                // Replacing a live connection: the user already holds a slot.
                _reserved[userId] = true;
                return true;
            }

            if (occupancy >= _capacity)
            {
                return false;
            }

            _reserved[userId] = false;
            _slotReservations++;
            return true;
        }
    }

    /// <summary>
    /// Register <paramref name="conn"/> and retire the reservation taken for its user.
    /// Returns false — the reservation is gone either way — in the one case a replacement
    /// can no longer be honoured: the connection being replaced was torn down while this
    /// join was in flight <i>and</i> the slot it freed has since gone to someone else.
    /// </summary>
    public bool Commit(Connection conn)
    {
        lock (_lock)
        {
            if (_reserved.Remove(conn.UserId, out bool replacement))
            {
                if (!replacement) _slotReservations--;
            }
            else
            {
                // No reservation on record: treat as a replacement if the user is
                // connected, otherwise as an uncounted join that must still fit.
                replacement = _connections.Get(conn.UserId) is not null;
            }

            // A slot reservation holds its slot until this line, so the add below cannot
            // exceed capacity. A replacement never held one; if its target is still
            // connected Add swaps it out and Count is unchanged, but if the target left
            // mid-join this connection needs a slot of its own and may not get one.
            if (_connections.Get(conn.UserId) is null && OccupancyLocked() >= _capacity)
            {
                return false;
            }

            _connections.Add(conn);
            return true;
        }
    }

    /// <summary>Give back a reservation that will not be committed. Idempotent.</summary>
    public void Release(string userId)
    {
        lock (_lock)
        {
            if (_reserved.Remove(userId, out bool replacement) && !replacement)
            {
                _slotReservations--;
            }
        }
    }
}
