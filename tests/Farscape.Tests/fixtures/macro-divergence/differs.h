/* Issue #56 fixture: FX_LIMIT differs from first.h; FX_SHARED agrees. */
#define FX_LIMIT 32
#define FX_SHARED 7
int fx_differs(int value);
