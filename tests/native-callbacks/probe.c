#include "probe.h"
static int observed;
int landing_invoke(LandingCallback callback) { return callback(4294967295U, -173L) == -7 ? 0 : 1; }
void landing_record(int value) { observed = value; }
int landing_invoke_void(LandingVoid callback) { observed = 0; callback(4294967295U, -173L); return observed == 346 ? 0 : 2; }
