# Branches onde ninguém commita nem dá push direto (ADR-016): só PR. Lista separada por espaço.
PROTEGIDAS="main-v2 dev-v2 main staging development refactor-v2"
E_protegida() {
  for p in $PROTEGIDAS; do
    [ "$1" = "$p" ] && return 0
  done
  return 1
}
