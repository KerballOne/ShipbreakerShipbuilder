import bpy
import os

textures_dir = r"C:\Users\user\Downloads\RocinanteShip\the-corvette-class-light-frigate-the-expanse\textures"

material_map = {
    "Aft_Section":     ["Aft_Section",           "Tachi_LP_Aft_Section"],
    "Fore_Section":    ["Fore_Section",           "Tachi_LP_Fore_Section"],
    "Nozzle_And_PDCs": ["Tachi_LP_Nozzle_And_PDCs"],
}

def load_tex(nodes, path, non_color=False):
    img = bpy.data.images.load(path, check_existing=True)
    if non_color:
        img.colorspace_settings.name = 'Non-Color'
    node = nodes.new('ShaderNodeTexImage')
    node.image = img
    return node

for mat in bpy.data.materials:
    prefixes = next((v for k, v in material_map.items() if k in mat.name), None)
    if prefixes is None:
        continue

    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    nodes.clear()

    bsdf = nodes.new('ShaderNodeBsdfPrincipled')
    out  = nodes.new('ShaderNodeOutputMaterial')
    links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])

    tex_files = {}
    for prefix in prefixes:
        for f in os.listdir(textures_dir):
            if f.startswith(prefix) and f.endswith(".png"):
                key = f.replace(prefix + "_", "").replace(".png", "")
                tex_files.setdefault(key, os.path.join(textures_dir, f))

    bc_node = None

    if "BaseColor" in tex_files:
        bc_node = load_tex(nodes, tex_files["BaseColor"])
        links.new(bc_node.outputs['Color'], bsdf.inputs['Base Color'])

    if "Metallic" in tex_files:
        n = load_tex(nodes, tex_files["Metallic"], non_color=True)
        links.new(n.outputs['Color'], bsdf.inputs['Metallic'])

    if "Roughness" in tex_files:
        n = load_tex(nodes, tex_files["Roughness"], non_color=True)
        links.new(n.outputs['Color'], bsdf.inputs['Roughness'])

    if "Normal" in tex_files:
        n = load_tex(nodes, tex_files["Normal"], non_color=True)
        nm = nodes.new('ShaderNodeNormalMap')
        links.new(n.outputs['Color'], nm.inputs['Color'])
        links.new(nm.outputs['Normal'], bsdf.inputs['Normal'])

    if "Emissive" in tex_files:
        n = load_tex(nodes, tex_files["Emissive"])
        links.new(n.outputs['Color'], bsdf.inputs['Emission Color'])
        bsdf.inputs['Emission Strength'].default_value = 1.0

    if "AO" in tex_files and bc_node is not None:
        ao = load_tex(nodes, tex_files["AO"], non_color=True)
        try:
            mix = nodes.new('ShaderNodeMix')
            mix.data_type = 'RGBA'
            mix.blend_type = 'MULTIPLY'
            mix.inputs['Factor'].default_value = 1.0
            links.new(bc_node.outputs['Color'], mix.inputs['A'])
            links.new(ao.outputs['Color'], mix.inputs['B'])
            links.new(mix.outputs['Result'], bsdf.inputs['Base Color'])
        except Exception:
            mix = nodes.new('ShaderNodeMixRGB')
            mix.blend_type = 'MULTIPLY'
            mix.inputs['Fac'].default_value = 1.0
            links.new(bc_node.outputs['Color'], mix.inputs['Color1'])
            links.new(ao.outputs['Color'], mix.inputs['Color2'])
            links.new(mix.outputs['Color'], bsdf.inputs['Base Color'])
