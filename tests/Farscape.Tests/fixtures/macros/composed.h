/* Object macros of every kind a pilot's macro_prefixes can select (#193, #192). */
#define FX_BASE 10
#define FX_COMPOSED (FX_BASE | (1<<8))
#define FX_ALIAS FX_COMPOSED
#define FX_NEGATIVE (-(FX_BASE * 3))
#define FX_NAME "fixture"
#define FX_API
#define FX_MAX(a, b) ((a) > (b) ? (a) : (b))
#define FX_RATIO 1.5
#define FX_HUGE (0xFFFFFFFFFFFFFFFFULL)
#define FX_BLOCK { 0 }

typedef void (*fx_destructor_type)(void *);
#define FX_STATIC ((fx_destructor_type)0)
#define FX_TRANSIENT ((fx_destructor_type)-1)

struct fx_registers { int control; };
#define FX_REGISTERS ((struct fx_registers *)0x40000000)

typedef struct fx_stmt fx_stmt;
int fx_bind_text(fx_stmt *statement, int index, const char *text, int bytes, void (*destructor)(void *));
