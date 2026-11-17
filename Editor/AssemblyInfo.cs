using System.Runtime.CompilerServices;

// EditMode tests kept in Tools~/EditModeTests (not shipped) reach the Manager's internal types.
[assembly: InternalsVisibleTo("OnAirDeck.UnityManager.EditModeTests")]

// The WARUDO protection build hook (Editor/WarudoProtect/UModHook, compiled only in WARUDO mod projects) calls the runner.
[assembly: InternalsVisibleTo("OnAirDeck.WarudoProtect.UModHook")]
