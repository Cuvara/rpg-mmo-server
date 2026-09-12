using UnityEngine;

/// <summary>
/// Quits the player a few seconds after the probe has run, so a batch invocation
/// terminates instead of sitting on a window forever.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>Il2cppTlsProbe</c>: the probe is the artefact under
/// review and is committed in the server repo; this is scaffolding for driving it and
/// must not end up entangled with what it measures.
/// </remarks>
public sealed class TlsProbeQuit : MonoBehaviour
{
    [SerializeField] private float secondsBeforeQuit = 12f;

    private void Start()
    {
        Invoke(nameof(Bye), secondsBeforeQuit);
    }

    private void Bye()
    {
        Debug.Log("[tls-probe] quitting");
        Application.Quit(0);
    }
}
