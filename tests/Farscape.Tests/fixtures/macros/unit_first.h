/* Two units (#192, #193): macros with the same text in both units over a private dependency
   each unit defines differently, and a sentinel only this unit defines. */
typedef void (*unit_destructor)(void *);
#define UNIT_PRIVATE -1
#define CODE_SHIFTED (UNIT_PRIVATE + 7)
#define SENT_DESTRUCTOR ((unit_destructor)UNIT_PRIVATE)
#define LOCAL_FIRST_ONLY ((unit_destructor)-1)
