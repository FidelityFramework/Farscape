#include "other_probe.h"
static int observed;
int landing_other_invoke(LandingCallback callback) { return callback(4294967295U, -290L) == -19 ? 0 : 3; }
void landing_other_record(int value) { observed = value; }
int landing_other_invoke_void(LandingVoid callback) { observed = 0; callback(4294967295U, -290L); return observed == 580 ? 0 : 4; }
