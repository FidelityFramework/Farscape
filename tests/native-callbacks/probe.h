typedef int (*LandingCallback)(unsigned int value, long delta);
typedef void (*LandingVoid)(unsigned int value, long delta);
int landing_invoke(LandingCallback callback);
int landing_invoke_void(LandingVoid callback);
void landing_record(int value);
