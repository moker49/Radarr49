import PropTypes from 'prop-types';
import React, { Component, Fragment } from 'react';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageSectionContent from 'Components/Page/PageSectionContent';
import PageToolbarButton from 'Components/Page/Toolbar/PageToolbarButton';
import PageToolbarSeparator from 'Components/Page/Toolbar/PageToolbarSeparator';
import { icons, inputTypes } from 'Helpers/Props';
import PendingChangesModal from 'Settings/PendingChangesModal';
import SettingsToolbar from 'Settings/SettingsToolbar';
import translate from 'Utilities/String/translate';
import QualityDefinitionsConnector from './Definition/QualityDefinitionsConnector';
import QualityProfileSizeDefinitionsConnector from './Definition/QualityProfileSizeDefinitionsConnector';
import ResetQualityDefinitionsModal from './Reset/ResetQualityDefinitionsModal';
import styles from './Quality.css';

class Quality extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveCallback = null;
    this._profileControls = null;

    this.state = {
      isSaving: false,
      hasPendingChanges: false,
      selectedProfileId: 0,
      nextProfileId: null,
      useGlobalValues: false,
      isConfirmQualityDefinitionResetModalOpen: false
    };
  }

  //
  // Listeners

  onChildMounted = (saveCallback, profileControls = null) => {
    this._saveCallback = saveCallback;
    this._profileControls = profileControls;
  };

  onChildStateChange = (payload) => {
    this.setState(payload);
  };

  onResetQualityDefinitionsPress = () => {
    this.setState({ isConfirmQualityDefinitionResetModalOpen: true });
  };

  onCloseResetQualityDefinitionsModal = () => {
    this.setState({ isConfirmQualityDefinitionResetModalOpen: false });
  };

  onSavePress = () => {
    if (this._saveCallback) {
      this._saveCallback();
    }
  };

  onProfileChange = ({ value }) => {
    const nextProfileId = Number(value);
    if (nextProfileId === this.state.selectedProfileId || this.state.isSaving) {
      return;
    }

    if (this.state.hasPendingChanges) {
      this.setState({ nextProfileId });
    } else {
      this.selectProfile(nextProfileId);
    }
  };

  selectProfile = (selectedProfileId) => {
    this._saveCallback = null;
    this._profileControls = null;
    this.setState({ selectedProfileId, nextProfileId: null, hasPendingChanges: false, isSaving: false });
  };

  onConfirmProfileChange = () => {
    this.selectProfile(this.state.nextProfileId);
  };

  onCancelProfileChange = () => {
    this.setState({ nextProfileId: null });
  };

  onUseGlobalChange = (change) => {
    this._profileControls?.onUseGlobalChange(change);
  };

  onCopyGlobalPress = () => {
    this._profileControls?.onCopyGlobal();
  };

  //
  // Render

  render() {
    const {
      isSaving,
      hasPendingChanges,
      selectedProfileId,
      nextProfileId,
      useGlobalValues
    } = this.state;
    const { profiles, isFetching, isPopulated, error, isResettingQualityDefinitions } = this.props;

    return (
      <PageContent title={translate('QualitySettings')}>
        <SettingsToolbar
          isSaving={isSaving}
          hasPendingChanges={hasPendingChanges}
          additionalButtons={
            selectedProfileId === 0 ? <Fragment>
              <PageToolbarSeparator />

              <PageToolbarButton
                label={translate('ResetDefinitions')}
                iconName={icons.REFRESH}
                isSpinning={isResettingQualityDefinitions}
                isDisabled={isSaving || hasPendingChanges}
                onPress={this.onResetQualityDefinitionsPress}
              />
            </Fragment> : null
          }
          onSavePress={this.onSavePress}
        />

        <PageContentBody>
          <PageSectionContent
            isFetching={isFetching}
            isPopulated={isPopulated}
            error={error}
            errorMessage={translate('QualityProfilesLoadError')}
          >
            <div className={styles.scopeControls}>
              <FormGroup>
                <FormLabel>{translate('SizeSettingsFor')}</FormLabel>
                <FormInputGroup
                  type={inputTypes.SELECT}
                  name="sizeSettingsProfile"
                  value={selectedProfileId}
                  values={[
                    { key: 0, value: translate('Global') },
                    ...profiles.map((profile) => ({ key: profile.id, value: profile.name }))
                  ]}
                  isDisabled={isSaving || isResettingQualityDefinitions}
                  onChange={this.onProfileChange}
                />
              </FormGroup>

              {selectedProfileId !== 0 ? <div className={styles.profileControls}>
                <FormInputGroup
                  type={inputTypes.CHECK}
                  name="useGlobalSizeDefinitions"
                  value={useGlobalValues}
                  helpText={translate('UseGlobalValues')}
                  isDisabled={isSaving}
                  onChange={this.onUseGlobalChange}
                />
                <Button isDisabled={isSaving || useGlobalValues} onPress={this.onCopyGlobalPress}>
                  {translate('CopyGlobalValues')}
                </Button>
              </div> : null}
            </div>

            {selectedProfileId === 0 ?
              <QualityDefinitionsConnector
                onChildMounted={this.onChildMounted}
                onChildStateChange={this.onChildStateChange}
              /> :
              <QualityProfileSizeDefinitionsConnector
                key={selectedProfileId}
                id={selectedProfileId}
                onChildMounted={this.onChildMounted}
                onChildStateChange={this.onChildStateChange}
              />}
          </PageSectionContent>
        </PageContentBody>

        <ResetQualityDefinitionsModal
          isOpen={this.state.isConfirmQualityDefinitionResetModalOpen}
          onModalClose={this.onCloseResetQualityDefinitionsModal}
        />

        <PendingChangesModal
          isOpen={nextProfileId !== null}
          onConfirm={this.onConfirmProfileChange}
          onCancel={this.onCancelProfileChange}
        />
      </PageContent>
    );
  }
}

Quality.propTypes = {
  profiles: PropTypes.arrayOf(PropTypes.object).isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isResettingQualityDefinitions: PropTypes.bool.isRequired
};

export default Quality;
