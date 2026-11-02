import PropTypes from 'prop-types';
import React from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import { inputTypes, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function QualityProfileSizeDefinitions({ qualityProfileItems, definitions, onChange }) {
  const values = definitions?.value || [];
  const qualities = qualityProfileItems.flatMap((item) => (item.quality ? [item] : item.items));

  function update(nextValues) {
    onChange({ name: 'sizeDefinitions', value: nextValues });
  }

  return (
    <div>
      <p>
        {translate('ProfileSizeSettingsHelp')}
      </p>

      {qualities.map(({ quality }) => {
        const definition = values.find((item) => item.qualityId === quality.id);

        return (
          <div key={quality.id}>
            <FormGroup size={sizes.EXTRA_SMALL}>
              <FormLabel size={sizes.SMALL}>{quality.name}</FormLabel>
              <FormInputGroup
                type={inputTypes.CHECK}
                name={`sizeOverride${quality.id}`}
                value={!!definition}
                helpText={translate('UseProfileSizeSettings')}
                onChange={({ value }) => update(value ? [
                  ...values,
                  { qualityId: quality.id, minSize: 0, preferredSize: null, maxSize: null }
                ] : values.filter((item) => item.qualityId !== quality.id))}
              />
            </FormGroup>

            {definition && [
              ['minSize', translate('ProfileMinimumSize')],
              ['preferredSize', translate('ProfilePreferredSize')],
              ['maxSize', translate('ProfileMaximumSize')]
            ].map(([field, label]) => (
              <FormGroup key={field} size={sizes.EXTRA_SMALL}>
                <FormLabel size={sizes.SMALL}>{label}</FormLabel>
                <FormInputGroup
                  type={inputTypes.NUMBER}
                  name={`${field}${quality.id}`}
                  value={definition[field]}
                  min={0}
                  max={2000}
                  isFloat={true}
                  onChange={({ value }) => update(values.map((item) => (item.qualityId === quality.id ? { ...item, [field]: value } : item)))}
                />
              </FormGroup>
            ))}
          </div>
        );
      })}

      {definitions?.errors?.map((error) => <p key={error.message}>{error.message}</p>)}
    </div>
  );
}

QualityProfileSizeDefinitions.propTypes = {
  qualityProfileItems: PropTypes.arrayOf(PropTypes.object).isRequired,
  definitions: PropTypes.object,
  onChange: PropTypes.func.isRequired
};

export default QualityProfileSizeDefinitions;
