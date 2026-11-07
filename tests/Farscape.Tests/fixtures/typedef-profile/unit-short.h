/* Issue #54 fixture: the second unit declares dev_t as unsigned short; its function must not
   take the first unit's (merged) declaration. */
typedef unsigned short dev_t;
int fx_second(dev_t device);
