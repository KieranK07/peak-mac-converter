"""Self-check for the asmdef versionDefines evaluator: python3 -I scripts/test_build_inputsystem.py"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_inputsystem as B

U = '6000.3.15f1'
for expr, want in [('6000.0.9', True), ('6000.1.0b9', True), ('6000.3.0a6', True), ('6000.4.0a4', False),
                   ('[2022.3.69,2023.0.0]', False), ('6000.3.15f1', True), ('6000.3.16f1', False),
                   ('(6000.3.15f1,6000.4)', False), ('[6000.3.15f1]', True), ('[6000.3.14f1]', False), ('1', True)]:
    assert B.in_range(U, expr) == want, expr
asmdef = {'versionDefines': [{'name': 'com.unity.modules.xr', 'expression': '1.0.0', 'define': 'XR'},
                             {'name': 'com.unity.xr.oculus', 'expression': '1.0.3', 'define': 'NO_OCULUS'},
                             {'name': 'Unity', 'expression': '6000.4.0a4', 'define': 'NEXT'},
                             {'name': 'Unity', 'expression': '6000.3.0a6', 'define': 'POLL'}]}
assert B.version_defines(asmdef, {'Unity': U, 'com.unity.modules.xr': '1.0.0'}) == ['POLL', 'XR']
print('ok')
