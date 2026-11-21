#pragma once

// Owned by the test process even after the plugin releases its loader reference.
struct Fixture {
    const char* scenario;
    unsigned creates = 0, systemQueries = 0, graphicsQueries = 0, destroys = 0;
    bool invalidCall = false;
};