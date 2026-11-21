#pragma once
#include <openxr/openxr.h>

template<class Query, class Now, class SleepFor>
XrResult waitForHeadset(Query query, Now millisecondsNow, SleepFor sleepMilliseconds) {
    const auto deadline = millisecondsNow() + 10000;
    for (;;) {
        const XrResult result = query();
        if (result != XR_ERROR_FORM_FACTOR_UNAVAILABLE || millisecondsNow() >= deadline) return result;
        sleepMilliseconds(250);
    }
}
