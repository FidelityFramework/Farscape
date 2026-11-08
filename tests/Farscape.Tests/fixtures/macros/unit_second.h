/* The second unit of unit_first.h's pair: the same macro text, UNIT_PRIVATE is 0 here, and
   the function whose sentinel parameter a pilot fixes is declared here. */
typedef void (*unit_destructor)(void *);
#define UNIT_PRIVATE 0
#define CODE_SHIFTED (UNIT_PRIVATE + 7)
#define SENT_DESTRUCTOR ((unit_destructor)UNIT_PRIVATE)
#define LOCAL_SECOND_ONLY ((unit_destructor)-1)
int unit_bind(char *text, unit_destructor destroy);
