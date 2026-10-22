grammar Mql5Grammar;

// ======================================================
// LEXER RULES
// ======================================================

// --- Channels ---
// Channel 0: DEFAULT (Parser sees this)
// Channel 1: PREPROCESSOR (Parser ignores, LSP can read)
// Channel 2: COMMENTS (Parser ignores, LSP uses for highlighting)

// --- Comments ---
COMMENT_BLOCK : '/*' .*? '*/' -> channel(2);
COMMENT_LINE  : '//' ~[\r\n]* -> channel(2);

// --- Whitespace ---
WS            : [ \t\r\n\u000C]+ -> skip;

// --- Preprocessor (Hybrid Strategy) ---
// Structural directives kept in parser for "Go to Definition"
PRE_INCLUDE : '#include' ~[\r\n]*;
PRE_PROPERTY: '#property' ~[\r\n]*;
PRE_IMPORT  : '#import' ~[\r\n]*;
PRE_RESOURCE: '#resource' ~[\r\n]*;
PRE_PACK    : '#pragma pack(' ~[\r\n]*;

// Logical/Macro directives hidden from parser to prevent breakage
PRE_DEFINE  : '#define' ~[\r\n]* -> channel(1);
PRE_IFDEF   : '#ifdef' ~[\r\n]* -> channel(1);
PRE_IFNDEF  : '#ifndef' ~[\r\n]* -> channel(1);
PRE_ELSE    : '#else' ~[\r\n]* -> channel(1);
PRE_ENDIF   : '#endif' ~[\r\n]* -> channel(1);
PRE_UNDEF   : '#undef' ~[\r\n]* -> channel(1);

// --- MQL4 Specific Literals ---
LITERAL_DATE  : 'D\'' ~[']* '\'';
LITERAL_COLOR : 'C\'' ~[']* '\'';

// --- Numbers ---
HEX           : '0' [xX] [0-9a-fA-F]+;
// Issue #176: optional f/F suffix on real literals (C-style; MetaEditor
// accepts `0.0f`/`1e3f` — official MQL5 Reference samples use f-suffixed
// float literals, e.g. the matrixf examples). Integer `123f` still lexes as
// INTEGER + IDENTIFIER.
DOUBLE        : [0-9]+ '.' [0-9]* EXP? [fF]?
              | '.' [0-9]+ EXP? [fF]?
              | [0-9]+ EXP [fF]?
              ;
INTEGER       : [0-9]+;

fragment EXP  : [Ee] [+-]? [0-9]+;

// --- Strings & Chars ---
STRING        : '"' (ESC | ~["\\])* '"';
CHAR          : '\'' (ESC | ~['\\]) '\'';
fragment ESC  : '\\' [abfnrtv\\'"0?];

// --- Keywords ---
K_INT       : 'int';
K_DOUBLE    : 'double';
K_STRING    : 'string';
K_BOOL      : 'bool';
K_VOID      : 'void';
K_DATETIME  : 'datetime';
K_COLOR     : 'color';
K_CHAR      : 'char';
K_UCHAR     : 'uchar';
K_SHORT     : 'short';
K_USHORT    : 'ushort';
K_UINT      : 'uint';
K_LONG      : 'long';
K_ULONG     : 'ulong';
K_FLOAT     : 'float';

K_STATIC    : 'static';
K_EXTERN    : 'extern';
K_INPUT     : 'input';
K_SINPUT    : 'sinput';
// Issue #176: `group` keyword for the documented input-parameter grouping
// (`input group "text"`, MQL5 Reference, inputs).
K_GROUP     : 'group';
K_CONST     : 'const';
K_VIRTUAL   : 'virtual';
K_OVERRIDE  : 'override';
K_FINAL     : 'final';
// Issue #141: `abstract` class modifier. NOTE: unlike `final`, `abstract` is
// not in the official MQL5 reserved-words list; classes become abstract via
// pure virtual functions. We still accept `abstract class ...` so documented
// style examples (and legacy authoring habits) parse without spurious errors.
K_ABSTRACT  : 'abstract';
// Issue #26: `inline` storage-class keyword on out-of-class member definitions
// (e.g. `inline bool CClass::Method(...)`). Valid MQL4/MQL5.
K_INLINE    : 'inline';

K_CLASS     : 'class';
K_STRUCT    : 'struct';
K_INTERFACE : 'interface';
K_UNION     : 'union';
K_PUBLIC    : 'public';
K_PRIVATE   : 'private';
K_PROTECTED : 'protected';
K_TEMPLATE  : 'template';
K_TYPENAME  : 'typename';
K_OPERATOR  : 'operator';

K_ENUM      : 'enum';
K_NEW       : 'new';
K_DELETE    : 'delete';
K_SIZEOF    : 'sizeof';
K_USING     : 'using';
K_NAMESPACE : 'namespace';
K_NULLPTR   : 'nullptr';

K_IF        : 'if';
K_ELSE      : 'else';
K_WHILE     : 'while';
K_FOR       : 'for';
K_DO        : 'do';
K_SWITCH    : 'switch';
K_CASE      : 'case';
K_DEFAULT   : 'default';
K_BREAK     : 'break';
K_CONTINUE  : 'continue';
K_RETURN    : 'return';

K_TRUE      : 'true';
K_FALSE     : 'false';
K_NULL      : 'NULL';

// --- Operators ---
SCOPE       : '::';
ASSIGN      : '=';
ASSIGN_ADD  : '+=';
ASSIGN_SUB  : '-=';
ASSIGN_MUL  : '*=';
ASSIGN_DIV  : '/=';
ASSIGN_MOD  : '%=';
ASSIGN_AND  : '&=';
ASSIGN_OR   : '|=';
ASSIGN_XOR  : '^=';
ASSIGN_LSH  : '<<=';
ASSIGN_RSH  : '>>=';

ARROW       : '->';
INC         : '++';
DEC         : '--';
ADD         : '+';
SUB         : '-';
MUL         : '*';
DIV         : '/';
MOD         : '%';

EQ          : '==';
NEQ         : '!=';
LTE         : '<=';
GTE         : '>=';
LT          : '<';
GT          : '>';

LOG_AND     : '&&';
LOG_OR      : '||';
LOG_NOT     : '!';

BIT_AND     : '&';
BIT_OR      : '|';
BIT_XOR     : '^';
BIT_NOT     : '~';
SHIFT_L     : '<<';
SHIFT_R     : '>>';

QUESTION    : '?';
COLON       : ':';
SEMICOLON   : ';';
COMMA       : ',';
DOT         : '.';

LPAREN      : '(';
RPAREN      : ')';
LBRACE      : '{';
RBRACE      : '}';
LBRACKET    : '[';
RBRACKET    : ']';

// --- Identifiers ---
IDENTIFIER  : [a-zA-Z_] [a-zA-Z0-9_]*;


// ======================================================
// PARSER RULES
// ======================================================

compilationUnit
    : translationUnit* EOF
    ;

translationUnit
    : templateDefinition
    | directive
    | inputGroupDirective
    | classDeclaration
    | structDeclaration
    | unionDeclaration
    | interfaceDeclaration
    | enumDeclaration
    | functionDeclaration
    | globalConstructorDeclaration
    | globalDestructorDeclaration
    | variableDeclarationStatement
    | usingDirective
    | semicolon
    ;

semicolon
    : SEMICOLON
    ;

// --- Directive Wrapper ---
directive
    : PRE_INCLUDE
    | PRE_PROPERTY
    | PRE_IMPORT
    | PRE_RESOURCE
    | PRE_PACK
    ;

// Issue #176: documented input-parameter grouping (`input group "text"`,
// MQL5 Reference, inputs). Exact documented form: no trailing semicolon;
// `sinput group` is not documented and stays rejected (#141 honesty rule).
inputGroupDirective
    : K_INPUT K_GROUP STRING
    ;

// --- Types ---
// Supports "int", "const int", "int&", "List<T>", and object pointers "T *p"
// (MUL declarator, issue #26).
type
    : modifiers? baseType (LT type (COMMA type)* GT)? (MUL | BIT_AND)?
    ;

baseType
    : K_INT | K_DOUBLE | K_STRING | K_BOOL | K_VOID
    | K_DATETIME | K_COLOR
    | K_CHAR | K_UCHAR | K_SHORT | K_USHORT | K_UINT | K_LONG | K_ULONG | K_FLOAT
    | qualifiedName
    ;

// Issue #143: `override` is a documented MQL5 access specifier
// (docs/basis/syntax/reserved) and a valid member modifier
// (`void F() override { }`). The lexer already had K_OVERRIDE; the parser
// just never matched it.
modifiers
    : (K_CONST | K_STATIC | K_INPUT | K_SINPUT | K_EXTERN | K_VIRTUAL | K_FINAL | K_OVERRIDE | K_INLINE)+
    ;

qualifiedName
    : (SCOPE)? IDENTIFIER (SCOPE IDENTIFIER)*
    ;

// --- Declarations ---

variableDeclarationStatement
    : variableDeclaration SEMICOLON
    ;

// FIX: Allows "int static x" by allowing modifiers after type
variableDeclaration
    : modifiers? type modifiers? variableDeclarator (COMMA variableDeclarator)*
    ;

variableDeclarator
    : IDENTIFIER arraySpecifier* (ASSIGN initializer | LPAREN argumentList? RPAREN)?
    ;

// FIX: Supports [2][4]
arraySpecifier
    : LBRACKET expression? RBRACKET
    ;

initializer
    : expression
    | arrayInitializer
    ;

arrayInitializer
    : LBRACE (initializer (COMMA initializer)*)? COMMA? RBRACE
    ;

// --- Functions ---
functionDeclaration
    : templateDefinition? modifiers? type modifiers? qualifiedName LPAREN parameterList? RPAREN modifiers? (block | (pureSpecifier? | deleteSpecifier) SEMICOLON)
    ;

// Issue #143: pure virtual specifier. The MQL5 compiler accepts exactly two
// spellings (compile error 381: "Illegal syntax when declaring pure virtual
// function, only \"=NULL\" or \"=0\" are allowed") — nothing else.
pureSpecifier
    : ASSIGN (INTEGER | K_NULL)
    ;

// Issue #145: `= delete` method specifier. Documented in the official
// MQL5Book (oop/classes_and_interfaces/classes_final_delete: "The delete
// keyword can be specified in the header of a method to make it
// inaccessible in the current class and its descendants") with the exact
// shape `void method() = delete;`. Kept separate from pureSpecifier:
// compile error 381 closes that rule to "=NULL"/"=0" only, and deletion
// is a different construct than purity. Not documented for MQL4
// (docs.mql4.com only carries `delete` as the memory operator), so this
// alternative is MQL5-only.
deleteSpecifier
    : ASSIGN K_DELETE
    ;

// Constructor outside of class: Crypter::Crypter() { }
globalConstructorDeclaration
    : modifiers? qualifiedName LPAREN parameterList? RPAREN (initializationList)? (block | SEMICOLON)
    ;

// Destructor outside of class: Crypter::~Crypter() { }
globalDestructorDeclaration
    : modifiers? qualifiedName SCOPE BIT_NOT IDENTIFIER LPAREN parameterList? RPAREN (block | SEMICOLON)
    ;

templateDefinition
    : K_TEMPLATE LT (K_TYPENAME | K_CLASS) IDENTIFIER (ASSIGN type)? GT
    ;

parameterList
    : parameter (COMMA parameter)*
    ;

// FIX: Robust handling of reference '&'
parameter
    : modifiers? type modifiers? BIT_AND? IDENTIFIER? arraySpecifier* (ASSIGN expression)?
    ;

// --- Classes & Structs & Unions & Interfaces ---
// Issue #141: documented class modifiers. Per the MQL5 reference
// (docs/basis/types/classes#final_class) and the official MQL5Book examples
// (`class Derived final : public Base`), `final` goes AFTER the class name,
// BEFORE the optional base clause. `abstract` is accepted as a leading
// modifier (`abstract class CAnimal { ... };`) so such declarations parse as
// a single declaration instead of relying on ANTLR error recovery.
classDeclaration
    : K_ABSTRACT? K_CLASS IDENTIFIER K_FINAL? (COLON accessModifier qualifiedName)? LBRACE classBody RBRACE SEMICOLON
    ;

// Issue #141: the reference documents the same `final` suffix for structures
// (docs/basis/types/classes#final_struct: `struct settings final { ... };`).
structDeclaration
    : K_STRUCT IDENTIFIER K_FINAL? (COLON accessModifier qualifiedName)? LBRACE classBody RBRACE SEMICOLON
    ;

unionDeclaration
    : K_UNION IDENTIFIER LBRACE classBody RBRACE SEMICOLON
    ;

interfaceDeclaration
    : K_INTERFACE IDENTIFIER (COLON accessModifier qualifiedName)? LBRACE classBody RBRACE SEMICOLON
    ;

classBody
    : classMember*
    ;

classMember
    : accessModifier COLON
    | constructorDeclaration
    | destructorDeclaration
    | functionDeclaration
    | variableDeclarationStatement
    | directive
    | semicolon
    ;

accessModifier
    : K_PUBLIC | K_PRIVATE | K_PROTECTED
    ;

constructorDeclaration
    : IDENTIFIER LPAREN parameterList? RPAREN (K_CONST)? (initializationList)? (block | SEMICOLON)
    ;

destructorDeclaration
    : BIT_NOT IDENTIFIER LPAREN parameterList? RPAREN (block | SEMICOLON)
    ;

initializationList
    : COLON constructorInitializer (COMMA constructorInitializer)*
    ;

constructorInitializer
    : IDENTIFIER LPAREN expression? RPAREN
    ;

// --- Using directive ---
usingDirective
    : K_USING (K_NAMESPACE qualifiedName | qualifiedName) SEMICOLON
    ;

// --- Enums ---
enumDeclaration
    : K_ENUM (K_CLASS)? IDENTIFIER LBRACE enumMember (COMMA enumMember)* COMMA? RBRACE SEMICOLON
    ;

enumMember
    : IDENTIFIER (ASSIGN expression)?
    ;

// --- Statements ---
block
    : LBRACE statement* RBRACE
    ;

statement
    : block
    | variableDeclarationStatement
    | expressionStatement
    | ifStatement
    | whileStatement
    | doWhileStatement
    | forStatement
    | switchStatement
    | flowControlStatement
    | directive
    | semicolon
    ;

expressionStatement
    : expression? SEMICOLON
    ;

ifStatement
    : K_IF LPAREN expression RPAREN statement (K_ELSE statement)?
    ;

whileStatement
    : K_WHILE LPAREN expression RPAREN statement
    ;

doWhileStatement
    : K_DO statement K_WHILE LPAREN expression RPAREN SEMICOLON
    ;

forStatement
    : K_FOR LPAREN forInit expression? SEMICOLON expressionList? RPAREN statement
    ;

forInit
    : variableDeclaration SEMICOLON
    | expressionList? SEMICOLON
    ;

expressionList
    : expression (COMMA expression)*
    ;

switchStatement
    : K_SWITCH LPAREN expression RPAREN LBRACE switchBlock* RBRACE
    ;

switchBlock
    : (K_CASE expression | K_DEFAULT) COLON statement*
    ;

flowControlStatement
    : K_BREAK SEMICOLON
    | K_CONTINUE SEMICOLON
    | K_RETURN expression? SEMICOLON
    ;

// --- Expressions ---
// Issue #26: functional primitive-type cast "double((datetime)x)" — the cast
// keyword used as a call-like prefix in argument position (valid MQL).
expression
    : primaryExpression                                     # atomExpr
    | expression (DOT | ARROW) IDENTIFIER                   # memberAccessExpr
    | expression LBRACKET expression RBRACKET               # arrayIndexExpr
    | expression LPAREN argumentList? RPAREN                # functionCallExpr
    | primitiveCast LPAREN expression RPAREN                # functionalCastExpr
    | expression (INC | DEC)                                # postfixExpr
    | (INC | DEC) expression                                # prefixExpr
    | (ADD | SUB | BIT_NOT | LOG_NOT | MUL) expression        # unaryExpr
    | LPAREN type RPAREN expression                         # castExpr
    | K_SIZEOF LPAREN (type | expression) RPAREN            # sizeofExpr
    | K_NEW type (LPAREN argumentList? RPAREN)?             # newExpr
    | K_DELETE expression                                   # deleteExpr
    | expression (MUL | DIV | MOD) expression               # mulDivExpr
    | expression (ADD | SUB) expression                     # addSubExpr
    | expression (SHIFT_L | SHIFT_R) expression             # bitShiftExpr
    | expression (LT | LTE | GT | GTE) expression           # relationalExpr
    | expression (EQ | NEQ) expression                      # equalityExpr
    | expression BIT_AND expression                         # bitAndExpr
    | expression BIT_XOR expression                         # bitXorExpr
    | expression BIT_OR expression                          # bitOrExpr
    | expression LOG_AND expression                         # logAndExpr
    | expression LOG_OR expression                          # logOrExpr
    | <assoc=right> expression QUESTION expression COLON expression # ternaryExpr
    | <assoc=right> expression assignmentOp expression      # assignmentExpr
    ;

// Numeric primitive type names usable as functional cast prefixes
// (e.g. `double((datetime)x)`, `int(y)`).
primitiveCast
    : K_INT | K_DOUBLE | K_BOOL
    | K_DATETIME | K_COLOR
    | K_CHAR | K_UCHAR | K_SHORT | K_USHORT | K_UINT | K_LONG | K_ULONG | K_FLOAT
    ;

primaryExpression
    : literal
    | qualifiedName
    | LPAREN expression RPAREN
    ;

argumentList
    : expression (COMMA expression)*
    ;

assignmentOp
    : ASSIGN | ASSIGN_ADD | ASSIGN_SUB | ASSIGN_MUL | ASSIGN_DIV | ASSIGN_MOD
    | ASSIGN_AND | ASSIGN_OR | ASSIGN_XOR | ASSIGN_LSH | ASSIGN_RSH
    ;

literal
    : INTEGER
    | DOUBLE
    | HEX
    | STRING
    | CHAR
    | LITERAL_DATE
    | LITERAL_COLOR
    | K_TRUE
    | K_FALSE
    | K_NULL
    | K_NULLPTR
    ;
