/* A result clang spells `typeof (1.L)`: the unit names no such type, and clang's canonical type
   of the result is long double, an 80-bit value in 16 bytes of storage on x86_64. */
__typeof__(1.0L) fx_extended(void);
