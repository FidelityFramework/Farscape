/* Issue #54 fixture: dev_t declared as long: the profile's width (64) with the opposite signedness. */
typedef long dev_t;
int fx_signed_dev(dev_t device);
