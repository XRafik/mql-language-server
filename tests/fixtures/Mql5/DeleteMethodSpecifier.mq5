// MQL5 fixture: Issue #145 — `= delete` method specifier
// Official MQL5Book example (oop/classes_and_interfaces/classes_final_delete),
// word-for-word shape: `void method() = delete;`.
// Regression guards: defined, declared and pure virtual members in the same
// file must keep parsing (the #143 pureSpecifier rule is untouched).
class Base {
public:
    void method() { Print(__FUNCSIG__); }
};

class Derived : public Base {
public:
    void method() = delete;
};

class CAnimal {
public:
    virtual void Sound() = 0;   // #143 regression guard
    virtual void Rest();        // declared member — regression guard
};

class CCat : public CAnimal {
public:
    virtual void Sound() { }    // defined member — regression guard
};

void OnStart() {
    Base *b;
    Derived d;
    b = GetPointer(d);
    b.method();
}
