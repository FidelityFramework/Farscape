typedef int (*LandingCallback)(unsigned int value, long delta);
typedef void (*LandingVoid)(unsigned int value, long delta);
int landing_other_invoke(LandingCallback callback);
int landing_other_invoke_void(LandingVoid callback);
void landing_other_record(int value);
