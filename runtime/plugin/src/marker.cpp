// Retained for previously installed managed marker versions during updates.
extern "C" int __cdecl AFTXR_GetMarkerVersion()
{
    return 1;
}

// Called before Unity consumes graphics options, potentially from both of the
// player's argument readers. A future implementation must be idempotent and
// return 1 only once the OpenXR rendering path can take ownership.
extern "C" int __cdecl AFTXR_PrepareLaunch()
{
    return 0;
}
