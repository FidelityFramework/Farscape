/* Issue #53 fixture: uses the incomplete fx_never by value through an include. */
#include "never-enum.h"
int fx_take_never(enum fx_never value);
