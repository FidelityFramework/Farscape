/* The shape of glibc's __atomic_wide_counter (bits/atomic_wide_counter.h): a union of a
   64-bit counter and an unnamed struct of two 32-bit halves, named only by its typedef. */
typedef union
{
  unsigned long long int __value64;
  struct
  {
    unsigned int __low;
    unsigned int __high;
  } __value32;
} fx_wide_counter;

void fx_counter_read(fx_wide_counter *counter);
