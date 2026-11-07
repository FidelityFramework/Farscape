/* Issues #53/#54 fixture: uses fx_shared_kind through an include; its own parse does not declare it. */
#include "kind-enum.h"
int fx_take_shared(enum fx_shared_kind value);
