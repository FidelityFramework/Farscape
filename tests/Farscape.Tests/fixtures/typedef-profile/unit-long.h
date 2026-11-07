/* Issue #54 fixture: the first unit declares dev_t as the LP64 profile does. */
typedef unsigned long dev_t;
int fx_first(dev_t device);
