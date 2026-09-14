#include "probe.h"
struct LandingToken { int value; };
static LandingToken token = { 173 };
static int observed;
LandingToken *landing_token(void) { return &token; }
void *landing_data(void) { return &token; }
int landing_read(LandingToken *p) { return p == &token ? p->value : -1; }
int landing_read_data(void *p) { return p == &token ? token.value : -1; }
int landing_invoke(LandingCallback cb, void *data) { return cb(&token, 4294967295U, data) == -7 ? 0 : 1; }
void landing_record(int value) { observed = value; }
int landing_invoke_void(LandingVoid cb, void *data) { observed = 0; cb(&token, data); return observed == 346 ? 0 : 2; }
