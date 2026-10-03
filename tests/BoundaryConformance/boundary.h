#ifndef FARSCAPE_BOUNDARY_CONFORMANCE_H
#define FARSCAPE_BOUNDARY_CONFORMANCE_H

/* A real libc scalar entry, independent of the opaque fixture below. */
int abs(int value);

typedef struct boundary_token *Token;
typedef int (*TokenCallback)(Token token, unsigned int value, void *data);
typedef struct BoundaryRecord {
    Token nullableToken;
    Token _Nonnull requiredToken;
    int count;
} BoundaryRecord;

/* The pilot supplies explicit nonnull and ownership assertions. */
Token token_open(void);
Token token_nonnull(void);
int token_read(Token token);
void token_release(Token token);
void *token_environment(void);
int token_visit(Token token, TokenCallback callback, void *data);

#endif
