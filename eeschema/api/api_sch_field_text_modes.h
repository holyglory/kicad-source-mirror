/* Copyright The KiCad Developers. SPDX-License-Identifier: GPL-3.0-or-later */
#ifndef API_SCH_FIELD_TEXT_MODES_H
#define API_SCH_FIELD_TEXT_MODES_H

#include <google/protobuf/any.pb.h>
#include <google/protobuf/message.h>
#include <schematic/schematic_types.pb.h>
#include <memory>
#include <vector>

// SCH_FIELD is reconstructed with multiline support by the native file reader.
// Unlike a label's own single-line text, disabling this field mode is not a
// persisted setting. Check the original request before any native assignment.
inline bool SchematicFieldTextModesArePersistable( const google::protobuf::Message& aMessage )
{
    using google::protobuf::Any;
    using google::protobuf::FieldDescriptor;
    if( aMessage.GetDescriptor() == kiapi::schematic::types::SchematicField::descriptor() )
    {
        kiapi::schematic::types::SchematicField field;
        field.CopyFrom( aMessage );
        return field.text().attributes().multiline();
    }
    if( aMessage.GetDescriptor() == Any::descriptor() )
    {
        Any any;
        any.CopyFrom( aMessage );
        const std::string type = any.type_url().substr( any.type_url().find_last_of( '/' ) + 1 );
        const auto* descriptor = google::protobuf::DescriptorPool::generated_pool()->FindMessageTypeByName( type );
        const auto* prototype = descriptor
                ? google::protobuf::MessageFactory::generated_factory()->GetPrototype( descriptor ) : nullptr;
        if( !prototype ) return false;
        std::unique_ptr<google::protobuf::Message> value( prototype->New() );
        return any.UnpackTo( value.get() ) && SchematicFieldTextModesArePersistable( *value );
    }
    const auto* reflection = aMessage.GetReflection();
    std::vector<const FieldDescriptor*> fields;
    reflection->ListFields( aMessage, &fields );
    for( const auto* field : fields )
    {
        if( field->cpp_type() != FieldDescriptor::CPPTYPE_MESSAGE ) continue;
        if( field->is_repeated() )
        {
            for( int index = 0; index < reflection->FieldSize( aMessage, field ); ++index )
                if( !SchematicFieldTextModesArePersistable(
                            reflection->GetRepeatedMessage( aMessage, field, index ) ) )
                    return false;
        }
        else if( !SchematicFieldTextModesArePersistable( reflection->GetMessage( aMessage, field ) ) )
            return false;
    }
    return true;
}

#endif
